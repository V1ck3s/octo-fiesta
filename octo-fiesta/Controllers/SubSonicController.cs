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
    private readonly ILogger<SubsonicController> _logger;
    
    public SubsonicController(
        IOptions<SubsonicSettings> subsonicSettings,
        IMusicMetadataService metadataService,
        ILocalLibraryService localLibraryService,
        IDownloadService downloadService,
        SubsonicRequestParser requestParser,
        SubsonicResponseBuilder responseBuilder,
        SubsonicModelMapper modelMapper,
        SubsonicProxyService proxyService,
        ILogger<SubsonicController> logger,
        PlaylistSyncService? playlistSyncService = null)
    {
        _subsonicSettings = subsonicSettings.Value;
        _metadataService = metadataService;
        _localLibraryService = localLibraryService;
        _downloadService = downloadService;
        _requestParser = requestParser;
        _responseBuilder = responseBuilder;
        _modelMapper = modelMapper;
        _proxyService = proxyService;
        _playlistSyncService = playlistSyncService;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url))
        {
            throw new Exception("Error: Environment variable SUBSONIC_URL is not set.");
        }
    }

    // Extract all parameters (query + body)
    private async Task<Dictionary<string, string>> ExtractAllParameters()
    {
        return await _requestParser.ExtractAllParametersAsync(Request);
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
        
        // Search playlists if enabled
        Task<List<ExternalPlaylist>> playlistTask = _subsonicSettings.EnableExternalPlaylists
            ? _metadataService.SearchPlaylistsAsync(cleanQuery, ac) // Use same limit as albums
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

        var localPath = await _localLibraryService.GetLocalPathForExternalSongAsync(provider!, externalId!);

        if (localPath != null && System.IO.File.Exists(localPath))
        {
            var stream = System.IO.File.OpenRead(localPath);
            return File(stream, GetContentType(localPath), enableRangeProcessing: true);
        }

        try
        {
            var downloadStream = await _downloadService.DownloadAndStreamAsync(provider!, externalId!, HttpContext.RequestAborted);
            return File(downloadStream, "audio/mpeg", enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to stream: {ex.Message}" });
        }
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
    /// Merges local and Deezer albums.
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
            
            // Fill artist info for each album (Deezer API doesn't include it in artist/albums endpoint)
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

        var deezerArtists = await _metadataService.SearchArtistsAsync(artistName, 1);
        var deezerAlbums = new List<Album>();
        
        if (deezerArtists.Count > 0)
        {
            var deezerArtist = deezerArtists[0];
            if (deezerArtist.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            {
                deezerAlbums = await _metadataService.GetArtistAlbumsAsync("deezer", deezerArtist.ExternalId!);
                
                // Fill artist info for each album (Deezer API doesn't include it in artist/albums endpoint)
                // Use local artist ID and name so albums link back to the local artist
                foreach (var album in deezerAlbums)
                {
                    if (string.IsNullOrEmpty(album.Artist))
                    {
                        album.Artist = artistName;
                    }
                    if (string.IsNullOrEmpty(album.ArtistId))
                    {
                        album.ArtistId = localArtistId;
                    }
                }
            }
        }

        var localAlbumNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in localAlbums)
        {
            if (album is Dictionary<string, object> dict && dict.TryGetValue("name", out var nameObj))
            {
                localAlbumNames.Add(nameObj?.ToString() ?? "");
            }
        }

        var mergedAlbums = localAlbums.ToList();
        foreach (var deezerAlbum in deezerAlbums)
        {
            if (!localAlbumNames.Contains(deezerAlbum.Title))
            {
                mergedAlbums.Add(_responseBuilder.ConvertAlbumToJson(deezerAlbum));
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
    /// Enriches local albums with Deezer songs.
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
        var deezerAlbums = await _metadataService.SearchAlbumsAsync(searchQuery, 5);
        Album? deezerAlbum = null;
        
        // Find matching album on Deezer (exact match first)
        foreach (var candidate in deezerAlbums)
        {
            if (candidate.Artist != null && 
                candidate.Artist.Equals(artistName, StringComparison.OrdinalIgnoreCase) &&
                candidate.Title.Equals(albumName, StringComparison.OrdinalIgnoreCase))
            {
                deezerAlbum = await _metadataService.GetAlbumAsync("deezer", candidate.ExternalId!);
                break;
            }
        }

        // Fallback to fuzzy match
        if (deezerAlbum == null)
        {
            foreach (var candidate in deezerAlbums)
            {
                if (candidate.Artist != null && 
                    candidate.Artist.Contains(artistName, StringComparison.OrdinalIgnoreCase) &&
                    (candidate.Title.Contains(albumName, StringComparison.OrdinalIgnoreCase) ||
                     albumName.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    deezerAlbum = await _metadataService.GetAlbumAsync("deezer", candidate.ExternalId!);
                    break;
                }
            }
        }

        if (deezerAlbum != null && deezerAlbum.Songs.Count > 0)
        {
            var localSongTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in localSongs)
            {
                if (song is Dictionary<string, object> dict && dict.TryGetValue("title", out var titleObj))
                {
                    localSongTitles.Add(titleObj?.ToString() ?? "");
                }
            }

            var mergedSongs = localSongs.ToList();
            foreach (var deezerSong in deezerAlbum.Songs)
            {
                if (!localSongTitles.Contains(deezerSong.Title))
                {
                    mergedSongs.Add(_responseBuilder.ConvertSongToJson(deezerSong));
                }
            }

            mergedSongs = mergedSongs
                .OrderBy(s => s is Dictionary<string, object> dict && dict.TryGetValue("track", out var track) 
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
                    coverUrl = album.CoverArtUrl;
                }
                break;
                
            case "song":
            default:
                // For songs, try to get from song first, then album
                var song = await _metadataService.GetSongAsync(coverProvider!, coverExternalId!);
                if (song?.CoverArtUrl != null)
                {
                    coverUrl = song.CoverArtUrl;
                }
                else
                {
                    // Fallback: try album with same ID (legacy behavior)
                    var albumFallback = await _metadataService.GetAlbumAsync(coverProvider!, coverExternalId!);
                    if (albumFallback?.CoverArtUrl != null)
                    {
                        coverUrl = albumFallback.CoverArtUrl;
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

            return Content(doc.ToString(), "application/xml");
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
    /// Stars (favorites) an item. For playlists, this triggers a full download.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/star")]
    [Route("rest/star.view")]
    public async Task<IActionResult> Star()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        
        // Check if this is a playlist
        var playlistId = parameters.GetValueOrDefault("id", "");
        
        if (!string.IsNullOrEmpty(playlistId) && PlaylistIdHelper.IsExternalPlaylist(playlistId))
        {
            if (_playlistSyncService == null)
            {
                return _responseBuilder.CreateError(format, 0, "Playlist functionality is not enabled");
            }
            
            _logger.LogInformation("Starring external playlist {PlaylistId}, triggering download", playlistId);
            
            // Trigger playlist download in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await _playlistSyncService.DownloadFullPlaylistAsync(playlistId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download playlist {PlaylistId}", playlistId);
                }
            });
            
            // Return success response immediately
            return _responseBuilder.CreateResponse(format, "starred", new { });
        }
        
        // For non-playlist items, relay to real Subsonic server
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

    // Generic endpoint to handle all subsonic API calls
    [HttpGet, HttpPost]
    [Route("{**endpoint}")]
    public async Task<IActionResult> GenericEndpoint(string endpoint)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        
        try
        {
            var result = await _proxyService.RelayAsync(endpoint, parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File(result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            // Return Subsonic-compatible error response
            return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
        }
    }
}