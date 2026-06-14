using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Lyrics;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Controllers;

[ApiController]
[Route("")]
public class SubsonicController : ControllerBase
{
    private readonly SubsonicSettings _subsonicSettings;
    private readonly IMusicMetadataService _metadataService;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IDownloadService _downloadService;
    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicModelMapper _modelMapper;
    private readonly SubsonicProxyService _proxyService;
    private readonly PlaylistSyncService? _playlistSyncService;
    private readonly ILyricsService? _lyricsService;
    private readonly ILogger<SubsonicController> _logger;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;

    public SubsonicController(
        IOptions<SubsonicSettings> subsonicSettings,
        IMusicMetadataService metadataService,
        ILocalLibraryService localLibraryService,
        IDownloadService downloadService,
        SubsonicRequestParser requestParser,
        SubsonicResponseBuilder responseBuilder,
        SubsonicModelMapper modelMapper,
        SubsonicProxyService proxyService,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<SubsonicController> logger,
        PlaylistSyncService? playlistSyncService = null,
        ILyricsService? lyricsService = null)
    {
        _subsonicSettings = subsonicSettings.Value;
        _metadataService = metadataService;
        _localLibraryService = localLibraryService;
        _downloadService = downloadService;
        _requestParser = requestParser;
        _responseBuilder = responseBuilder;
        _modelMapper = modelMapper;
        _proxyService = proxyService;
        _hostApplicationLifetime = hostApplicationLifetime;
        _playlistSyncService = playlistSyncService;
        _lyricsService = lyricsService;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url))
        {
            throw new Exception("Error: Environment variable SUBSONIC_URL is not set.");
        }
    }
    /// <summary>
    /// Simple health check for root path to return HTTP 200. Some clients need this (ex. Amperfy)
    /// </summary>
    [HttpGet]
    [Route("")]
    public IActionResult Index()
    {
        return Ok(new { status = "ok" });
    }
    // Extract all parameters (query + body) and capture credentials for server-to-server calls
    private async Task<Dictionary<string, string>> ExtractAllParameters()
    {
        var parameters = await _requestParser.ExtractAllParametersAsync(Request);
        _localLibraryService.SetSubsonicCredentials(parameters);
        return parameters;
    }

    /// <summary>
    /// Merges local and external search results.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/search3")]
    [Route("rest/search3.view")]
    public async Task<IActionResult> Search3()
    {
        var parameters = await ExtractAllParameters();
        var query = parameters.GetValueOrDefault("query", "");
        var format = parameters.GetValueOrDefault("f", "xml");
        
        var cleanQuery = query.Trim().Trim('"');
        
        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            try
            {
                var result = await _proxyService.RelayAsync("rest/search3", parameters);
                var contentType = result.ContentType ?? $"application/{format}";
                return File(result.Body, contentType);
            }
            catch
            {
                return _responseBuilder.CreateResponse(format, "searchResult3", new { });
            }
        }

        var subsonicTask = _proxyService.RelaySafeAsync("rest/search3", parameters);
        var externalTask = _metadataService.SearchAllAsync(
            cleanQuery,
            int.TryParse(parameters.GetValueOrDefault("songCount", "20"), out var sc) ? sc : 20,
            int.TryParse(parameters.GetValueOrDefault("albumCount", "20"), out var ac) ? ac : 20,
            int.TryParse(parameters.GetValueOrDefault("artistCount", "20"), out var arc) ? arc : 20
        );
        
        // Playlists are merged into the album section (search3 has no playlist field),
        // so the limit is capped low to avoid masking real albums.
        Task<List<ExternalPlaylist>> playlistTask = _subsonicSettings.EnableExternalPlaylists
            ? _metadataService.SearchPlaylistsAsync(cleanQuery, Math.Min(ac, 5))
            : Task.FromResult(new List<ExternalPlaylist>());

        await Task.WhenAll(subsonicTask, externalTask, playlistTask);

        var subsonicResult = await subsonicTask;
        var externalResult = await externalTask;
        var playlistResult = await playlistTask;

        return MergeSearchResults(subsonicResult, externalResult, playlistResult, format);
    }

    /// <summary>
    /// Downloads on-the-fly if needed.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/stream")]
    [Route("rest/stream.view")]
    public async Task<IActionResult> Stream()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");

        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Missing id parameter" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (!isExternal)
        {
            return await _proxyService.RelayStreamAsync(parameters, HttpContext.RequestAborted);
        }

        // Always go through DownloadAndStreamAsync for external songs
        // This ensures quality upgrade logic is applied
        try
        {
            // Allow cancellation from both client disconnect and application shutdown
            using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                HttpContext.RequestAborted,
                _hostApplicationLifetime.ApplicationStopping);

            var (downloadStream, filePath) = await _downloadService.DownloadAndStreamAsync(provider!, externalId!, cancellationTokenSource.Token);
            return File(downloadStream, GetContentType(filePath), enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to stream: {ex.Message}" });
        }
    }

    /// <summary>
    /// OpenSubsonic extension used by some clients (e.g. Feishin) to decide whether
    /// a track can be played directly or needs transcoding. For external songs we
    /// return a direct-play response so the client proceeds to /rest/stream.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getTranscodeDecision")]
    [Route("rest/getTranscodeDecision.view")]
    public async Task<IActionResult> GetTranscodeDecision()
    {
        var parameters = await ExtractAllParameters();
        var mediaId = parameters.GetValueOrDefault("mediaId", "");
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            mediaId = parameters.GetValueOrDefault("id", "");
        }
        var format = parameters.GetValueOrDefault("f", "xml");

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(mediaId);
        if (!isExternal)
        {
            try
            {
                var result = await _proxyService.RelayRequestAsync("rest/getTranscodeDecision", Request, HttpContext.RequestAborted);
                if (result.StatusCode >= 400)
                {
                    return StatusCode(result.StatusCode);
                }
                var contentType = result.ContentType ?? $"application/{format}";
                return File(result.Body, contentType);
            }
            catch (HttpRequestException ex)
            {
                return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
            }
        }

        var protocol = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var song = await _metadataService.GetSongAsync(provider!, externalId!);
        if (song != null)
        {
            var mapping = await _localLibraryService.GetMappingForExternalSongAsync(provider!, externalId!);
            if (mapping != null)
            {
                song.LocalPath = mapping.LocalPath;
            }
        }

        return _responseBuilder.CreateTranscodeDecisionResponse(song, protocol);
    }

    /// <summary>
    /// Returns external song info if needed.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getSong")]
    [Route("rest/getSong.view")]
    public async Task<IActionResult> GetSong()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(id))
        {
            return _responseBuilder.CreateError(format, 10, "Missing id parameter");
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (!isExternal)
        {
            var result = await _proxyService.RelayAsync("rest/getSong", parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File(result.Body, contentType);
        }

        var song = await _metadataService.GetSongAsync(provider!, externalId!);

        if (song == null)
        {
            return _responseBuilder.CreateError(format, 70, "Song not found");
        }

        return _responseBuilder.CreateSongResponse(format, song);
    }

    /// <summary>
    /// OpenSubsonic getLyricsBySongId. Local tracks are answered by the backing Subsonic
    /// server (which reads embedded and external .lrc lyrics). For an external, not-yet-local
    /// track we fetch synced lyrics live (LRCLIB) so the client shows them on the first listen,
    /// before the file has been downloaded and indexed.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getLyricsBySongId")]
    [Route("rest/getLyricsBySongId.view")]
    public async Task<IActionResult> GetLyricsBySongId()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        var format = parameters.GetValueOrDefault("f", "xml");

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        // Local track, or lyrics feature disabled: let the real Subsonic server answer.
        if (!isExternal || _lyricsService is not { Enabled: true })
        {
            try
            {
                var result = await _proxyService.RelayAsync("rest/getLyricsBySongId", parameters);
                var contentType = result.ContentType ?? $"application/{format}";
                return File(result.Body, contentType);
            }
            catch (HttpRequestException ex)
            {
                return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
            }
        }

        var song = await _metadataService.GetSongAsync(provider!, externalId!);
        if (song == null)
        {
            return _responseBuilder.CreateLyricsBySongIdResponse(format, null);
        }

        var lyrics = await _lyricsService.GetLyricsAsync(song, HttpContext.RequestAborted);
        return _responseBuilder.CreateLyricsBySongIdResponse(format, lyrics);
    }

    /// <summary>
    /// Merges local and external albums.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getArtist")]
    [Route("rest/getArtist.view")]
    public async Task<IActionResult> GetArtist()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(id))
        {
            return _responseBuilder.CreateError(format, 10, "Missing id parameter");
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (isExternal)
        {
            var artist = await _metadataService.GetArtistAsync(provider!, externalId!);
            if (artist == null)
            {
                return _responseBuilder.CreateError(format, 70, "Artist not found");
            }

            var albums = await _metadataService.GetArtistAlbumsAsync(provider!, externalId!);
            
            // Fill artist info for each album (external API may not include it in artist/albums endpoint)
            foreach (var album in albums)
            {
                if (string.IsNullOrEmpty(album.Artist))
                {
                    album.Artist = artist.Name;
                }
                if (string.IsNullOrEmpty(album.ArtistId))
                {
                    album.ArtistId = artist.Id;
                }
            }
            
            return _responseBuilder.CreateArtistResponse(format, artist, albums);
        }

        var navidromeResult = await _proxyService.RelaySafeAsync("rest/getArtist", parameters);
        
        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return _responseBuilder.CreateError(format, 70, "Artist not found");
        }

        var navidromeContent = Encoding.UTF8.GetString(navidromeResult.Body);
        string artistName = "";
        string localArtistId = id; // Keep the local artist ID for merged albums
        var localAlbums = new List<object>();
        object? artistData = null;

        if (format == "json" || navidromeResult.ContentType?.Contains("json") == true)
        {
            var jsonDoc = JsonDocument.Parse(navidromeContent);
            if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                response.TryGetProperty("artist", out var artistElement))
            {
                artistName = artistElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                artistData = _responseBuilder.ConvertSubsonicJsonElement(artistElement, true);
                
                if (artistElement.TryGetProperty("album", out var albums))
                {
                    foreach (var album in albums.EnumerateArray())
                    {
                        localAlbums.Add(_responseBuilder.ConvertSubsonicJsonElement(album, true));
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(artistName) || artistData == null)
        {
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/json");
        }

        var externalArtists = await _metadataService.SearchArtistsAsync(artistName, 1);
        var externalAlbums = new List<Album>();
        
        if (externalArtists.Count > 0)
        {
            var externalArtist = externalArtists[0];
            if (externalArtist.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            {
                externalAlbums = await _metadataService.GetArtistAlbumsAsync(externalArtist.ExternalProvider!, externalArtist.ExternalId!);
                
                // Fill artist info for each album (external API may not include it in artist/albums endpoint)
                // Use local artist ID and name so albums link back to the local artist
                foreach (var album in externalAlbums)
                {
                    if (string.IsNullOrEmpty(album.Artist))
                    {
                        album.Artist = artistName;
                    }
                    album.ArtistId = localArtistId;
                }
            }
        }

        var localAlbumNames = new HashSet<string>();
        foreach (var album in localAlbums)
        {
            if (album is Dictionary<string, object> dict && dict.TryGetValue("name", out var nameObj))
            {
                var normalizedName = StringNormalizer.CreateComparisonKey(nameObj?.ToString() ?? "");
                localAlbumNames.Add(normalizedName);
            }
        }

        var mergedAlbums = localAlbums.ToList();
        foreach (var externalAlbum in externalAlbums)
        {
            var normalizedExternalName = StringNormalizer.CreateComparisonKey(externalAlbum.Title);
            if (!localAlbumNames.Contains(normalizedExternalName))
            {
                mergedAlbums.Add(_responseBuilder.ConvertAlbumToJson(externalAlbum));
            }
        }

        if (artistData is Dictionary<string, object> artistDict)
        {
            artistDict["album"] = mergedAlbums;
            artistDict["albumCount"] = mergedAlbums.Count;
        }

        return _responseBuilder.CreateJsonResponse(new
        {
            status = "ok",
            version = "1.16.1",
            artist = artistData
        });
    }

    /// <summary>
    /// Enriches local albums with external songs.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getAlbum")]
    [Route("rest/getAlbum.view")]
    public async Task<IActionResult> GetAlbum()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(id))
        {
            return _responseBuilder.CreateError(format, 10, "Missing id parameter");
        }
        
        // Check if this is an external playlist
        if (PlaylistIdHelper.IsExternalPlaylist(id))
        {
            try
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);
                
                // Get playlist metadata
                var playlist = await _metadataService.GetPlaylistAsync(provider, externalId);
                if (playlist == null)
                {
                    return _responseBuilder.CreateError(format, 70, "Playlist not found");
                }
                
                // Get playlist tracks
                var tracks = await _metadataService.GetPlaylistTracksAsync(provider, externalId);
                
                // Add all tracks to playlist cache so when they're played, we know they belong to this playlist
                if (_playlistSyncService != null)
                {
                    foreach (var track in tracks)
                    {
                        if (!string.IsNullOrEmpty(track.ExternalId))
                        {
                            var trackId = $"ext-{provider}-{track.ExternalId}";
                            _playlistSyncService.AddTrackToPlaylistCache(trackId, id);
                        }
                    }
                    
                    _logger.LogDebug("Added {TrackCount} tracks to playlist cache for {PlaylistId}", tracks.Count, id);
                }
                
                // Convert to album response (playlist as album)
                return _responseBuilder.CreatePlaylistAsAlbumResponse(format, playlist, tracks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting playlist {Id}", id);
                return _responseBuilder.CreateError(format, 70, "Playlist not found");
            }
        }

        var (isExternal, albumProvider, albumExternalId) = _localLibraryService.ParseSongId(id);

        if (isExternal)
        {
            var album = await _metadataService.GetAlbumAsync(albumProvider!, albumExternalId!);

            if (album == null)
            {
                return _responseBuilder.CreateError(format, 70, "Album not found");
            }

            return _responseBuilder.CreateAlbumResponse(format, album);
        }

        var navidromeResult = await _proxyService.RelaySafeAsync("rest/getAlbum", parameters);
        
        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return _responseBuilder.CreateError(format, 70, "Album not found");
        }

        var navidromeContent = Encoding.UTF8.GetString(navidromeResult.Body);
        string albumName = "";
        string artistName = "";
        var localSongs = new List<object>();
        object? albumData = null;

        if (format == "json" || navidromeResult.ContentType?.Contains("json") == true)
        {
            var jsonDoc = JsonDocument.Parse(navidromeContent);
            if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                response.TryGetProperty("album", out var albumElement))
            {
                albumName = albumElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                artistName = albumElement.TryGetProperty("artist", out var artist) ? artist.GetString() ?? "" : "";
                albumData = _responseBuilder.ConvertSubsonicJsonElement(albumElement, true);
                
                if (albumElement.TryGetProperty("song", out var songs))
                {
                    foreach (var song in songs.EnumerateArray())
                    {
                        localSongs.Add(_responseBuilder.ConvertSubsonicJsonElement(song, true));
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(albumName) || string.IsNullOrEmpty(artistName) || albumData == null)
        {
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/json");
        }

        var searchQuery = $"{artistName} {albumName}";
        var externalAlbumsSearch = await _metadataService.SearchAlbumsAsync(searchQuery, 5);
        Album? externalAlbum = null;
        
        // Find matching album on external service (exact match first)
        foreach (var candidate in externalAlbumsSearch)
        {
            if (candidate.Artist != null && 
                candidate.Artist.Equals(artistName, StringComparison.OrdinalIgnoreCase) &&
                candidate.Title.Equals(albumName, StringComparison.OrdinalIgnoreCase))
            {
                externalAlbum = await _metadataService.GetAlbumAsync(candidate.ExternalProvider!, candidate.ExternalId!);
                break;
            }
        }

        // Fallback to fuzzy match
        if (externalAlbum == null)
        {
            foreach (var candidate in externalAlbumsSearch)
            {
                if (candidate.Artist != null && 
                    candidate.Artist.Contains(artistName, StringComparison.OrdinalIgnoreCase) &&
                    (candidate.Title.Contains(albumName, StringComparison.OrdinalIgnoreCase) ||
                     albumName.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    externalAlbum = await _metadataService.GetAlbumAsync(candidate.ExternalProvider!, candidate.ExternalId!);
                    break;
                }
            }
        }

        if (externalAlbum != null && externalAlbum.Songs.Count > 0)
        {
            var localSongTitles = new HashSet<string>();
            foreach (var song in localSongs)
            {
                if (song is Dictionary<string, object> dict && dict.TryGetValue("title", out var titleObj))
                {
                    var normalizedTitle = StringNormalizer.CreateComparisonKey(titleObj?.ToString() ?? "");
                    localSongTitles.Add(normalizedTitle);
                }
            }

            var mergedSongs = localSongs.ToList();
            foreach (var externalSong in externalAlbum.Songs)
            {
                var normalizedExternalTitle = StringNormalizer.CreateComparisonKey(externalSong.Title);
                if (!localSongTitles.Contains(normalizedExternalTitle))
                {
                    mergedSongs.Add(_responseBuilder.ConvertSongToJson(externalSong));
                }
            }

            mergedSongs = mergedSongs
                .OrderBy(s => s is Dictionary<string, object> dict && dict.TryGetValue("discNumber", out var discNumber)
                    ? Convert.ToInt32(discNumber)
                    : 0)
                .ThenBy(s => s is Dictionary<string, object> dict && dict.TryGetValue("track", out var track)
                    ? Convert.ToInt32(track)
                    : 0)
                .ToList();

            if (albumData is Dictionary<string, object> albumDict)
            {
                albumDict["song"] = mergedSongs;
                albumDict["songCount"] = mergedSongs.Count;
                
                var totalDuration = 0;
                foreach (var song in mergedSongs)
                {
                    if (song is Dictionary<string, object> dict && dict.TryGetValue("duration", out var dur))
                    {
                        totalDuration += Convert.ToInt32(dur);
                    }
                }
                albumDict["duration"] = totalDuration;
            }
        }

        return _responseBuilder.CreateJsonResponse(new
        {
            status = "ok",
            version = "1.16.1",
            album = albumData
        });
    }

    /// <summary>
    /// Proxies external covers. Uses type from ID to determine which API to call.
    /// Format: ext-{provider}-{type}-{id} (e.g., ext-deezer-artist-259, ext-deezer-album-96126)
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getCoverArt")]
    [Route("rest/getCoverArt.view")]
    public async Task<IActionResult> GetCoverArt()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");

        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }
        
        // Check if this is a playlist cover art request
        if (PlaylistIdHelper.IsExternalPlaylist(id))
        {
            try
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);
                var playlist = await _metadataService.GetPlaylistAsync(provider, externalId);
                
                if (playlist == null || string.IsNullOrEmpty(playlist.CoverUrl))
                {
                    return NotFound();
                }
                
                // Download and return the cover image
                var imageResponse = await new HttpClient().GetAsync(playlist.CoverUrl);
                if (!imageResponse.IsSuccessStatusCode)
                {
                    return NotFound();
                }
                
                var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync();
                var contentType = imageResponse.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                return File(imageBytes, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting playlist cover art for {Id}", id);
                return NotFound();
            }
        }

        var (isExternal, coverProvider, type, coverExternalId) = _localLibraryService.ParseExternalId(id);

        if (!isExternal)
        {
            try
            {
                var result = await _proxyService.RelayAsync("rest/getCoverArt", parameters);
                var contentType = result.ContentType ?? "image/jpeg";
                return File(result.Body, contentType);
            }
            catch
            {
                return NotFound();
            }
        }

        string? coverUrl = null;
        
        // Use type to determine which API to call first
        switch (type)
        {
            case "artist":
                var artist = await _metadataService.GetArtistAsync(coverProvider!, coverExternalId!);
                if (artist?.ImageUrl != null)
                {
                    coverUrl = artist.ImageUrl;
                }
                break;
                
            case "album":
                var album = await _metadataService.GetAlbumAsync(coverProvider!, coverExternalId!);
                if (album?.CoverArtUrl != null)
                {
                    coverUrl = album.CoverArtUrlLarge ?? album.CoverArtUrl;
                }
                break;
                
            case "song":
            default:
                // For songs, try to get from song first, then album
                var song = await _metadataService.GetSongAsync(coverProvider!, coverExternalId!);
                if (song?.CoverArtUrl != null)
                {
                    coverUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl;
                }
                else
                {
                    // Fallback: try album with same ID (legacy behavior)
                    var albumFallback = await _metadataService.GetAlbumAsync(coverProvider!, coverExternalId!);
                    if (albumFallback?.CoverArtUrl != null)
                    {
                        coverUrl = albumFallback.CoverArtUrlLarge ?? albumFallback.CoverArtUrl;
                    }
                }
                break;
        }
        
        if (coverUrl != null)
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(coverUrl);
            if (response.IsSuccessStatusCode)
            {
                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                return File(imageBytes, contentType);
            }
        }

        return NotFound();
    }

    #region Helper Methods

    private IActionResult MergeSearchResults(
        (byte[]? Body, string? ContentType, bool Success) subsonicResult,
        SearchResult externalResult,
        List<ExternalPlaylist> playlistResult,
        string format)
    {
        var (localSongs, localAlbums, localArtists) = subsonicResult.Success && subsonicResult.Body != null
            ? _modelMapper.ParseSearchResponse(subsonicResult.Body, subsonicResult.ContentType)
            : (new List<object>(), new List<object>(), new List<object>());

        var isJson = format == "json" || subsonicResult.ContentType?.Contains("json") == true;
        var (mergedSongs, mergedAlbums, mergedArtists) = _modelMapper.MergeSearchResults(
            localSongs, 
            localAlbums, 
            localArtists, 
            externalResult,
            playlistResult,
            isJson);

        if (isJson)
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                status = "ok",
                version = "1.16.1",
                searchResult3 = new
                {
                    song = mergedSongs,
                    album = mergedAlbums,
                    artist = mergedArtists
                }
            });
        }
        else
        {
            var ns = XNamespace.Get("http://subsonic.org/restapi");
            var searchResult3 = new XElement(ns + "searchResult3");
            
            foreach (var artist in mergedArtists.Cast<XElement>())
            {
                searchResult3.Add(artist);
            }
            foreach (var album in mergedAlbums.Cast<XElement>())
            {
                searchResult3.Add(album);
            }
            foreach (var song in mergedSongs.Cast<XElement>())
            {
                searchResult3.Add(song);
            }

            var doc = new XDocument(
                new XElement(ns + "subsonic-response",
                    new XAttribute("status", "ok"),
                    new XAttribute("version", "1.16.1"),
                    searchResult3
                )
            );

            return Content(doc.ToString(), "application/xml; charset=utf-8");
        }
    }

    private string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".aac" => "audio/aac",
            _ => "audio/mpeg"
        };
    }

    #endregion

    /// <summary>
    /// Stars (favorites) an item. For external playlists and albums, this triggers a full download.
    /// In Cache mode, starring an external song moves it from cache to permanent storage.
    /// External song IDs are resolved to local Subsonic IDs when possible.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/star")]
    [Route("rest/star.view")]
    public async Task<IActionResult> Star()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");

        // Check if this is a playlist
        var playlistId = GetExternalPlaylistIdFromStarParameters(parameters);
        if (!string.IsNullOrEmpty(playlistId) && PlaylistIdHelper.IsExternalPlaylist(playlistId))
        {
            if (_playlistSyncService == null)
            {
                return _responseBuilder.CreateError(format, 0, "Playlist functionality is not enabled");
            }
            
            _logger.LogInformation("Starring external playlist {PlaylistId}, triggering download", playlistId);
            
            // In Cache mode, download directly to permanent storage
            var forcePermanent = _subsonicSettings.StorageMode == StorageMode.Cache;
            
            // Trigger playlist download in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await _playlistSyncService.DownloadFullPlaylistAsync(playlistId, forcePermanent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download playlist {PlaylistId}", playlistId);
                }
            });
            
            // Return success response immediately
            return _responseBuilder.CreateResponse(format, "starred", new { });
        }

        var (isExternalAlbum, albumProvider, albumExternalId, rawAlbumId) = GetExternalAlbumFromStarParameters(parameters);
        if (isExternalAlbum)
        {
            _logger.LogInformation("Starring external album {AlbumId}, triggering full download", rawAlbumId);
            // In Cache mode, download directly to permanent storage
            if (_subsonicSettings.StorageMode == StorageMode.Cache)
            {
                _downloadService.DownloadFullAlbumInBackgroundToPermanent(albumProvider!, albumExternalId!);
            }
            else
            {
                _downloadService.DownloadFullAlbumInBackground(albumProvider!, albumExternalId!);
            }
            return _responseBuilder.CreateResponse(format, "starred", new { });
        }

        // Check if this is an external song in Cache mode
        if (_subsonicSettings.StorageMode == StorageMode.Cache && parameters.TryGetValue("id", out var id))
        {
            var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(id);
            if (isExternal && string.Equals(type, "song", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(externalId))
            {
                _logger.LogInformation("Starring external song in Cache mode: {Provider}:{ExternalId}", provider, externalId);
                
                var permanentized = await _downloadService.PermanentizeCachedSongAsync(provider, externalId);
                if (permanentized)
                {
                    _logger.LogInformation("Successfully permanentized cached song {Provider}:{ExternalId}", provider, externalId);
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogInformation("Scheduling downloading a song {Provider}:{ExternalId}", provider, externalId);
                            await _downloadService.DownloadSongToPermanentAsync(provider, externalId, _hostApplicationLifetime.ApplicationStopping);
                            _logger.LogInformation("Successfully downloaded song {Provider}:{ExternalId}", provider,
                                externalId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,"Failed to download starred song");
                        }
                    });

                }
                // Return success - the song will be available locally after Navidrome scans
                return _responseBuilder.CreateResponse(format, "starred", new { });
            }
        }

        // In Permanent mode, resolve external song IDs to local Subsonic IDs
        var starResolution = await ResolveExternalSongIdIfPossible(parameters, "star");
        if (starResolution is { IsExternalSong: true, Resolved: false })
        {
            // Song isn't local yet: download it. DownloadSongToPermanentAsync cascades to the
            // full album in Album download mode.
            var (_, songProvider, _, songExternalId) = _localLibraryService.ParseExternalId(parameters["id"]);
            if (!string.IsNullOrEmpty(songProvider) && !string.IsNullOrEmpty(songExternalId))
            {
                _logger.LogInformation(
                    "Starring external song not yet local: {Provider}:{ExternalId}, triggering download",
                    songProvider, songExternalId);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _downloadService.DownloadSongToPermanentAsync(
                            songProvider, songExternalId, _hostApplicationLifetime.ApplicationStopping);
                        _logger.LogInformation("Successfully downloaded starred song {Provider}:{ExternalId}",
                            songProvider, songExternalId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to download starred song {Provider}:{ExternalId}",
                            songProvider, songExternalId);
                    }
                });

                return _responseBuilder.CreateResponse(format, "starred", new { });
            }

            return _responseBuilder.CreateError(format, 70,
                "External song could not be starred because it is not available locally yet.");
        }
        
        // For non-external items or Permanent mode, relay to real Subsonic server
        try
        {
            var result = await _proxyService.RelayAsync("rest/star", parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File(result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes favorite from an item. External song IDs are resolved to local Subsonic IDs when possible.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/unstar")]
    [Route("rest/unstar.view")]
    public async Task<IActionResult> Unstar()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");

        var unstarResolution = await ResolveExternalSongIdIfPossible(parameters, "unstar");
        if (unstarResolution is { IsExternalSong: true, Resolved: false })
        {
            return _responseBuilder.CreateError(format, 70,
                "External song could not be unstarred because it is not available locally.");
        }

        try
        {
            var result = await _proxyService.RelayAsync("rest/unstar", parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File(result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
        }
    }

    /// <summary>
    /// Scrobbles a song. External song IDs are resolved to local Subsonic IDs when possible.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/scrobble")]
    [Route("rest/scrobble.view")]
    public async Task<IActionResult> Scrobble()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");

        var scrobbleResolution = await ResolveExternalSongIdIfPossible(parameters, "scrobble");
        if (scrobbleResolution is { IsExternalSong: true, Resolved: false })
        {
            return _responseBuilder.CreateResponse(format, "scrobble", new { });
        }

        try
        {
            var result = await _proxyService.RelayAsync("rest/scrobble", parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File(result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates a playlist. External song IDs are resolved to local Subsonic IDs.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/updatePlaylist")]
    [Route("rest/updatePlaylist.view")]
    public async Task<IActionResult> UpdatePlaylist()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");

        if (parameters.TryGetValue("songIdToAdd", out var rawSongIdToAdd) && !string.IsNullOrWhiteSpace(rawSongIdToAdd))
        {
            var requestedSongIds = rawSongIdToAdd
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (requestedSongIds.Length > 0)
            {
                var resolvedSongIds = new List<string>(requestedSongIds.Length);

                foreach (var songId in requestedSongIds)
                {
                    var resolvedId = await ResolvePlaylistSongIdAsync(songId, HttpContext.RequestAborted);
                    if (string.IsNullOrEmpty(resolvedId))
                    {
                        return _responseBuilder.CreateError(format, 70,
                            $"Could not add external song '{songId}' to playlist: local track not available");
                    }

                    resolvedSongIds.Add(resolvedId);
                }

                parameters["songIdToAdd"] = string.Join(',', resolvedSongIds);
            }
        }

        try
        {
            var result = await _proxyService.RelayAsync("rest/updatePlaylist", parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File(result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
        }
    }

    private string GetExternalPlaylistIdFromStarParameters(Dictionary<string, string> parameters)
    {
        // Clients may send the playlist ID as "id" or "albumId" depending on the client
        // (playlists are presented as albums, so most clients use "albumId")
        var id = parameters.GetValueOrDefault("id", "");
        if (!string.IsNullOrEmpty(id) && PlaylistIdHelper.IsExternalPlaylist(id))
        {
            return id;
        }

        var albumId = parameters.GetValueOrDefault("albumId", "");
        if (!string.IsNullOrEmpty(albumId) && PlaylistIdHelper.IsExternalPlaylist(albumId))
        {
            return albumId;
        }

        return string.Empty;
    }

    private async Task<(bool IsExternalSong, bool Resolved)> ResolveExternalSongIdIfPossible(Dictionary<string, string> parameters, string endpoint)
    {
        if (!parameters.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return (false, false);
        }

        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(id);
        if (!isExternal || !string.Equals(type, "song", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(externalId))
        {
            return (false, false);
        }

        var localId = await _localLibraryService.GetLocalIdForExternalSongAsync(provider, externalId);
        if (!string.IsNullOrEmpty(localId))
        {
            _logger.LogInformation("Resolved {Endpoint} ID {ExternalId} to local ID {LocalId}", endpoint, id, localId);
            parameters["id"] = localId;
            return (true, true);
        }

        _logger.LogInformation("Could not resolve external {Endpoint} ID {ExternalId} to a local ID", endpoint, id);
        return (true, false);
    }

    private (bool IsExternalAlbum, string? Provider, string? ExternalId, string RawAlbumId) GetExternalAlbumFromStarParameters(Dictionary<string, string> parameters)
    {
        var id = parameters.GetValueOrDefault("id", "");
        if (TryParseExternalAlbumId(id, out var provider, out var externalId))
        {
            return (true, provider, externalId, id);
        }

        var albumId = parameters.GetValueOrDefault("albumId", "");
        if (TryParseExternalAlbumId(albumId, out provider, out externalId))
        {
            return (true, provider, externalId, albumId);
        }

        return (false, null, null, string.Empty);
    }

    private bool TryParseExternalAlbumId(string id, out string? provider, out string? externalId)
    {
        provider = null;
        externalId = null;

        if (string.IsNullOrWhiteSpace(id) || PlaylistIdHelper.IsExternalPlaylist(id))
        {
            return false;
        }

        var (isExternal, parsedProvider, type, parsedExternalId) = _localLibraryService.ParseExternalId(id);
        if (!isExternal || !string.Equals(type, "album", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(parsedProvider) || string.IsNullOrEmpty(parsedExternalId))
        {
            return false;
        }

        provider = parsedProvider;
        externalId = parsedExternalId;
        return true;
    }

    private async Task<string?> ResolvePlaylistSongIdAsync(string songId, CancellationToken cancellationToken)
    {
        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(songId);

        if (!isExternal || !string.Equals(type, "song", StringComparison.OrdinalIgnoreCase))
        {
            return songId;
        }

        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(externalId))
        {
            return null;
        }

        // Song already has a local Subsonic ID.
        var localId = await _localLibraryService.GetLocalIdForExternalSongAsync(provider, externalId);
        if (!string.IsNullOrEmpty(localId))
        {
            return localId;
        }

        _logger.LogInformation("Song {SongId} is not available locally yet. Downloading before playlist update.", songId);
        if (!await _downloadService.PermanentizeCachedSongAsync(provider, externalId, cancellationToken))
        {
           await _downloadService.DownloadSongToPermanentAsync(provider, externalId, cancellationToken);
        }

        localId = await _localLibraryService.WaitForLocalIdAfterScanAsync(provider, externalId, cancellationToken);
        if (!string.IsNullOrEmpty(localId))
        {
            return localId;
        }

        _logger.LogWarning(
            "Could not resolve local Subsonic ID for external song {Provider}:{ExternalId} after download and scan",
            provider,
            externalId);

        return null;
    }

    // Generic endpoint to handle all subsonic API calls
    [HttpGet, HttpPost]
    [Route("{**endpoint}")]
    public async Task<IActionResult> GenericEndpoint(string endpoint)
    {
        // Capture credentials from any request (including catch-all)
        var parameters = await ExtractAllParameters();
        
        try
        {
            var result = await _proxyService.RelayRequestAsync(endpoint, Request, HttpContext.RequestAborted);
            
            if (result.StatusCode >= 400)
            {
                return StatusCode(result.StatusCode);
            }
            
            var contentType = result.ContentType ?? "application/xml; charset=utf-8";
            return File(result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            var format = parameters.GetValueOrDefault("f", "xml");
            return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
        }
    }
}