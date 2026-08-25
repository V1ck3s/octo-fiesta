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
using octo_fiesta.Services.SquidWTF;
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

        // Serve an already-owned copy from the library instead of re-downloading.
        // Skipped when AutoUpgradeQuality is on so the download path can still
        // upgrade a lower-quality local copy on play.
        if (!_subsonicSettings.AutoUpgradeQuality)
        {
            var localSongId = await _localLibraryService.GetLocalIdForExternalSongAsync(provider!, externalId!);
            if (!string.IsNullOrEmpty(localSongId))
            {
                parameters["id"] = localSongId;
                return await _proxyService.RelayStreamAsync(parameters, HttpContext.RequestAborted);
            }
        }

        // Otherwise download from the provider and stream (quality upgrade logic applies)
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

        var localSongId = await _localLibraryService.GetLocalIdForExternalSongAsync(provider!, externalId!);
        if (!string.IsNullOrEmpty(localSongId))
        {
            parameters["id"] = localSongId;
            var localResult = await _proxyService.RelayAsync("rest/getSong", parameters);
            var localContentType = localResult.ContentType ?? $"application/{format}";
            return File(localResult.Body, localContentType);
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

            // The library can hold albums the provider does not list, and a client that
            // navigated here from an external track would otherwise never see them.
            var ownedAlbums = await GetLocalArtistAlbumsAsync(artist.Name, parameters);
            if (ownedAlbums.Count > 0)
            {
                var ownedTitles = ownedAlbums
                    .Select(a => StringNormalizer.CreateComparisonKey(a.Title))
                    .ToHashSet();

                albums = ownedAlbums
                    .Concat(albums.Where(a => !ownedTitles.Contains(StringNormalizer.CreateComparisonKey(a.Title))))
                    .ToList();
            }

            return _responseBuilder.CreateArtistResponse(format, artist, albums);
        }

        var navidromeResult = await _proxyService.RelaySafeAsync("rest/getArtist", parameters);
        
        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return _responseBuilder.CreateError(format, 70, "Artist not found");
        }

        var navidromeContent = Encoding.UTF8.GetString(navidromeResult.Body);
        var isJson = format == "json" || navidromeResult.ContentType?.Contains("json") == true;
        string artistName = "";
        string localArtistId = id; // Keep the local artist ID for merged albums
        var localAlbums = new List<object>();
        object? artistData = null;
        XElement? artistXml = null;

        if (isJson)
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
        else
        {
            artistXml = ParseNavidromeXmlElement(navidromeContent, "artist");
            artistName = artistXml?.Attribute("name")?.Value ?? "";
        }

        if (string.IsNullOrEmpty(artistName) || (isJson ? artistData == null : artistXml == null))
        {
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/json");
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
        foreach (var album in ChildElements(artistXml, "album"))
        {
            localAlbumNames.Add(StringNormalizer.CreateComparisonKey(album.Attribute("name")?.Value));
        }

        var candidates = (await _metadataService.SearchArtistsAsync(artistName, 20))
            .Where(a => !string.IsNullOrEmpty(a.ExternalId) && a.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.AlbumCount ?? 0)
            .ThenByDescending(a => string.Equals(a.Name, artistName, StringComparison.Ordinal))
            .ToList();

        var externalAlbums = new List<Album>();
        List<Album>? firstCandidateAlbums = null;

        foreach (var candidate in candidates.Take(5))
        {
            var candidateAlbums = await _metadataService.GetArtistAlbumsAsync(candidate.ExternalProvider!, candidate.ExternalId!);
            firstCandidateAlbums ??= candidateAlbums;

            if (localAlbumNames.Count == 0 ||
                candidateAlbums.Any(a => localAlbumNames.Contains(StringNormalizer.CreateComparisonKey(a.Title))))
            {
                externalAlbums = candidateAlbums;
                break;
            }
        }

        if (externalAlbums.Count == 0 && firstCandidateAlbums != null)
        {
            externalAlbums = firstCandidateAlbums;
        }

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

        var newAlbums = externalAlbums
            .Where(a => !localAlbumNames.Contains(StringNormalizer.CreateComparisonKey(a.Title)))
            .ToList();

        // XML clients get the Navidrome artist element back untouched, external albums
        // appended, so their local albums keep every attribute the server sent.
        if (!isJson)
        {
            var ns = XNamespace.Get("http://subsonic.org/restapi");
            foreach (var externalAlbum in newAlbums)
            {
                artistXml!.Add(_responseBuilder.ConvertAlbumToXml(externalAlbum, ns));
            }
            artistXml!.SetAttributeValue("albumCount", ChildElements(artistXml, "album").Count());

            var doc = new XDocument(
                new XElement(ns + "subsonic-response",
                    new XAttribute("status", "ok"),
                    new XAttribute("version", "1.16.1"),
                    artistXml));

            return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
        }

        var mergedAlbums = localAlbums
            .Concat(newAlbums.Select(a => _responseBuilder.ConvertAlbumToJson(a)))
            .ToList();

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

    private static readonly string[] CollaborationWords = { "feat", "featuring", "ft", "with", "and", "x" };

    /// <summary>
    /// True when a candidate album is credited to the same artist, allowing the provider to
    /// spell out collaborators the library leaves out, as in "No Etiquette feat. Rayna" or
    /// "Dion &amp; The Belmonts". A homonym like "Gary Grimes" does not extend "Grimes" and
    /// is rejected.
    /// </summary>
    private static bool IsSameArtistOrCollaboration(string? candidateArtist, string artistName)
    {
        var candidateKey = StringNormalizer.CreateComparisonKey(candidateArtist);
        var artistKey = StringNormalizer.CreateComparisonKey(artistName);

        if (candidateKey.Length == 0 || artistKey.Length == 0)
        {
            return false;
        }

        if (candidateKey == artistKey)
        {
            return true;
        }

        if (!ExtendsAtWordBoundary(candidateKey, artistKey))
        {
            return false;
        }

        var suffix = candidateKey[artistKey.Length..].TrimStart();
        if (suffix.StartsWith('&') || suffix.StartsWith(','))
        {
            return true;
        }

        var firstWord = suffix.Split(' ')[0].Trim('.');
        return CollaborationWords.Contains(firstWord);
    }

    /// <summary>
    /// True when one title is the other followed by an edition suffix, such as "Visions" and
    /// "Visions (Deluxe Edition)". The suffix has to open on punctuation, so a longer title
    /// that keeps naming things, like "The Best Of Dion &amp; The Belmonts", stays a distinct
    /// album, and so does a title that merely contains the other, like "Starhand Visions".
    /// </summary>
    private static bool IsSameAlbumWithEditionSuffix(string? candidateTitle, string albumName)
    {
        var candidateKey = StringNormalizer.CreateComparisonKey(candidateTitle);
        var albumKey = StringNormalizer.CreateComparisonKey(albumName);

        if (candidateKey.Length == 0 || albumKey.Length == 0)
        {
            return false;
        }

        return HasEditionSuffix(candidateKey, albumKey) || HasEditionSuffix(albumKey, candidateKey);
    }

    private static bool HasEditionSuffix(string longer, string shorter)
    {
        if (!ExtendsAtWordBoundary(longer, shorter))
        {
            return false;
        }

        var suffix = longer[shorter.Length..].TrimStart();
        return suffix.StartsWith('(') || suffix.StartsWith('[') || suffix.StartsWith('-') || suffix.StartsWith(':');
    }

    private static bool ExtendsAtWordBoundary(string longer, string shorter)
    {
        return longer.Length > shorter.Length
            && longer.StartsWith(shorter, StringComparison.Ordinal)
            && !char.IsLetterOrDigit(longer[shorter.Length]);
    }

    /// <summary>
    /// Merges the provider catalogue into an artist's top songs.
    /// </summary>
    /// <remarks>
    /// Navidrome answers getTopSongs by intersecting the Last.fm ranking with the
    /// local library, so a sparse library returns one or two titles even when the
    /// provider carries the whole discography. Without this route the call fell
    /// through to the catch-all proxy and external results never reached the client.
    /// </remarks>
    [HttpGet, HttpPost]
    [Route("rest/getTopSongs")]
    [Route("rest/getTopSongs.view")]
    public async Task<IActionResult> GetTopSongs()
    {
        var parameters = await ExtractAllParameters();
        var artistName = parameters.GetValueOrDefault("artist", "");
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(artistName))
        {
            return _responseBuilder.CreateError(format, 10, "Missing artist parameter");
        }

        var count = int.TryParse(parameters.GetValueOrDefault("count", "50"), out var parsedCount) && parsedCount > 0
            ? parsedCount
            : 50;

        var navidromeTask = _proxyService.RelaySafeAsync("rest/getTopSongs", parameters);
        var externalTask = SearchArtistCatalogSafeAsync(artistName, count);

        await Task.WhenAll(navidromeTask, externalTask);

        var navidromeResult = await navidromeTask;
        var externalSongs = await externalTask;

        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return _responseBuilder.CreateResponse(format, "topSongs", new { });
        }

        // The merge below only builds JSON, so XML clients keep the untouched
        // relay. Same contract as getArtist.
        var isJson = format == "json" || navidromeResult.ContentType?.Contains("json") == true;
        if (!isJson)
        {
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/xml");
        }

        var mergedSongs = new List<object>();
        var seenTitles = new HashSet<string>();

        try
        {
            using var jsonDoc = JsonDocument.Parse(Encoding.UTF8.GetString(navidromeResult.Body));
            if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                response.TryGetProperty("topSongs", out var topSongs) &&
                topSongs.TryGetProperty("song", out var songs) &&
                songs.ValueKind == JsonValueKind.Array)
            {
                foreach (var song in songs.EnumerateArray())
                {
                    mergedSongs.Add(_responseBuilder.ConvertSubsonicJsonElement(song, true));

                    if (song.TryGetProperty("title", out var title))
                    {
                        seenTitles.Add(StringNormalizer.CreateComparisonKey(title.GetString()));
                    }
                }
            }
        }
        catch (JsonException)
        {
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/json");
        }

        foreach (var song in externalSongs)
        {
            if (mergedSongs.Count >= count)
            {
                break;
            }

            if (!IsCreditedTo(song.Artist, artistName))
            {
                continue;
            }

            if (!seenTitles.Add(StringNormalizer.CreateComparisonKey(song.Title)))
            {
                continue;
            }

            mergedSongs.Add(_responseBuilder.ConvertSongToJson(song));
        }

        if (mergedSongs.Count > count)
        {
            mergedSongs = mergedSongs.Take(count).ToList();
        }

        return _responseBuilder.CreateJsonResponse(new
        {
            status = "ok",
            version = "1.16.1",
            topSongs = new { song = mergedSongs }
        });
    }

    /// <summary>
    /// Separators that mark the end of a leading artist credit.
    /// </summary>
    private static readonly string[] CreditSeparators =
        new[] { "&", ",", "/", "feat", "ft.", "with ", "and ", "x " };

    /// <summary>
    /// Looks the artist up on the provider. A provider outage must not cost the
    /// user the local songs, so failures degrade to an empty list.
    /// </summary>
    private async Task<List<Song>> SearchArtistCatalogSafeAsync(string artistName, int count)
    {
        try
        {
            // Ask wide: the provider ranks by relevance to the query, which mixes in
            // namesakes, and IsCreditedTo filters those out afterwards.
            return await _metadataService.SearchSongsAsync(artistName, Math.Max(count, 20) * 2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "getTopSongs: provider lookup failed for {Artist}", artistName);
            return new List<Song>();
        }
    }

    /// <summary>
    /// True when the credit is the requested artist, alone or leading a
    /// collaboration such as "Serge Gainsbourg &amp; Jane Birkin". Searching a
    /// provider for one artist also surfaces namesakes - "Charlotte Gainsbourg"
    /// for "Serge Gainsbourg" - so a plain substring test would let them through.
    /// </summary>
    private static bool IsCreditedTo(string? candidate, string artistName)
    {
        var candidateKey = StringNormalizer.CreateComparisonKey(candidate);
        var artistKey = StringNormalizer.CreateComparisonKey(artistName);

        if (candidateKey.Length == 0 || artistKey.Length == 0)
        {
            return false;
        }

        if (candidateKey == artistKey)
        {
            return true;
        }

        if (!candidateKey.StartsWith(artistKey, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = candidateKey[artistKey.Length..].TrimStart();
        return CreditSeparators.Any(separator => remainder.StartsWith(separator, StringComparison.Ordinal));
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

        var (isExternal, albumProvider, albumType, albumExternalId) = _localLibraryService.ParseExternalId(id);

        if (isExternal)
        {
            // Amazon Music via squidwtf: songs lacking an album ASIN use albumId=songId so clients
            // can look up cover art. Synthesise a single-track album so the client can queue/play.
            // Scoped to squidwtf to avoid touching the getAlbum path for other providers.
            if (albumType == "song" && albumProvider == "squidwtf")
            {
                var song = await _metadataService.GetSongAsync(albumProvider!, albumExternalId!);
                if (song == null)
                    return _responseBuilder.CreateError(format, 70, "Album not found");

                var syntheticAlbum = new octo_fiesta.Models.Domain.Album
                {
                    Id = id,
                    Title = song.Album ?? song.Title,
                    Artist = song.Artist,
                    ArtistId = song.ArtistId,
                    CoverArtUrl = song.CoverArtUrl,
                    CoverArtUrlLarge = song.CoverArtUrlLarge,
                    IsLocal = false,
                    ExternalProvider = albumProvider,
                    ExternalId = albumExternalId,
                    Songs = new System.Collections.Generic.List<octo_fiesta.Models.Domain.Song> { song }
                };
                return _responseBuilder.CreateAlbumResponse(format, syntheticAlbum);
            }

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
        var isJson = format == "json" || navidromeResult.ContentType?.Contains("json") == true;
        string albumName = "";
        string artistName = "";
        var localSongs = new List<object>();
        object? albumData = null;
        XElement? albumXml = null;

        if (isJson)
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

        else
        {
            albumXml = ParseNavidromeXmlElement(navidromeContent, "album");
            albumName = albumXml?.Attribute("name")?.Value ?? "";
            artistName = albumXml?.Attribute("artist")?.Value ?? "";
        }

        if (string.IsNullOrEmpty(albumName) || string.IsNullOrEmpty(artistName) ||
            (isJson ? albumData == null : albumXml == null))
        {
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/json");
        }

        var searchQuery = $"{artistName} {albumName}";
        var externalAlbumsSearch = await _metadataService.SearchAlbumsAsync(searchQuery, 5);
        Album? externalAlbum = null;

        // Only a candidate credited to the same artist can be merged, otherwise a homonym
        // such as "Gary Grimes" pours its tracks into an album by "Grimes".
        var sameArtistCandidates = externalAlbumsSearch
            .Where(c => IsSameArtistOrCollaboration(c.Artist, artistName))
            .ToList();

        var albumKey = StringNormalizer.CreateComparisonKey(albumName);
        var match = sameArtistCandidates
                .FirstOrDefault(c => StringNormalizer.CreateComparisonKey(c.Title) == albumKey)
            ?? sameArtistCandidates
                .FirstOrDefault(c => IsSameAlbumWithEditionSuffix(c.Title, albumName));

        if (match != null)
        {
            externalAlbum = await _metadataService.GetAlbumAsync(match.ExternalProvider!, match.ExternalId!);
        }

        var localSongTitles = new HashSet<string>();
        foreach (var song in localSongs)
        {
            if (song is Dictionary<string, object> dict && dict.TryGetValue("title", out var titleObj))
            {
                localSongTitles.Add(StringNormalizer.CreateComparisonKey(titleObj?.ToString() ?? ""));
            }
        }
        foreach (var song in ChildElements(albumXml, "song"))
        {
            localSongTitles.Add(StringNormalizer.CreateComparisonKey(song.Attribute("title")?.Value));
        }

        var newSongs = externalAlbum?.Songs
            .Where(s => !localSongTitles.Contains(StringNormalizer.CreateComparisonKey(s.Title)))
            .ToList() ?? new List<Song>();

        // XML clients get the Navidrome album element back untouched, missing tracks
        // appended, so their local songs keep every attribute the server sent.
        if (!isJson)
        {
            if (newSongs.Count == 0)
            {
                return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/xml");
            }

            var ns = XNamespace.Get("http://subsonic.org/restapi");
            var albumId = albumXml!.Attribute("id")?.Value;
            var songElements = ChildElements(albumXml, "song").ToList();
            songElements.AddRange(newSongs.Select(s => _responseBuilder.ConvertSongToXml(s, ns, albumId)));

            foreach (var songElement in ChildElements(albumXml, "song").ToList())
            {
                songElement.Remove();
            }

            var orderedSongs = songElements
                .OrderBy(e => XmlAttributeInt(e, "discNumber"))
                .ThenBy(e => XmlAttributeInt(e, "track"))
                .ToList();

            albumXml.Add(orderedSongs);
            albumXml.SetAttributeValue("songCount", orderedSongs.Count);
            albumXml.SetAttributeValue("duration", orderedSongs.Sum(e => XmlAttributeInt(e, "duration")));

            var doc = new XDocument(
                new XElement(ns + "subsonic-response",
                    new XAttribute("status", "ok"),
                    new XAttribute("version", "1.16.1"),
                    albumXml));

            return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
        }

        if (newSongs.Count > 0 && albumData is Dictionary<string, object> albumDict)
        {
            var mergedSongs = localSongs
                .Concat(newSongs.Select(s => _responseBuilder.ConvertSongToJson(s)))
                .OrderBy(s => s is Dictionary<string, object> dict && dict.TryGetValue("discNumber", out var discNumber)
                    ? Convert.ToInt32(discNumber)
                    : 0)
                .ThenBy(s => s is Dictionary<string, object> dict && dict.TryGetValue("track", out var track)
                    ? Convert.ToInt32(track)
                    : 0)
                .ToList();

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
                if (coverUrl == null)
                {
                    var song = await _metadataService.GetSongAsync(coverProvider!, coverExternalId!);
                    if (song?.CoverArtUrl != null)
                    {
                        coverUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl;
                    }
                    else
                    {
                        var albumFallback = await _metadataService.GetAlbumAsync(coverProvider!, coverExternalId!);
                        if (albumFallback?.CoverArtUrl != null)
                        {
                            coverUrl = albumFallback.CoverArtUrlLarge ?? albumFallback.CoverArtUrl;
                        }
                    }
                }
                break;
        }
        
        if (coverUrl != null)
        {
            using var httpClient = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, coverUrl);

            var response = await httpClient.SendAsync(req);
            if (response.IsSuccessStatusCode)
            {
                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                return File(imageBytes, contentType);
            }
            _logger.LogWarning("Cover art fetch failed for {Url}: HTTP {Status}", coverUrl, (int)response.StatusCode);
        }

        return NotFound();
    }

    #region Helper Methods

    /// <summary>
    /// Extracts a named element of a Subsonic XML response, or null when the payload is
    /// not the expected answer.
    /// </summary>
    private XElement? ParseNavidromeXmlElement(string content, string localName)
    {
        try
        {
            var root = XDocument.Parse(content).Root;
            if (root == null || root.Attribute("status")?.Value != "ok")
            {
                return null;
            }

            return root.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        }
        catch (System.Xml.XmlException ex)
        {
            _logger.LogDebug(ex, "Could not parse the Subsonic {LocalName} XML response", localName);
            return null;
        }
    }

    private static IEnumerable<XElement> ChildElements(XElement? parent, string localName)
        => parent?.Elements().Where(e => e.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

    private static int XmlAttributeInt(XElement element, string name)
        => int.TryParse(element.Attribute(name)?.Value, out var value) ? value : 0;

    /// <summary>
    /// Returns the albums the library owns for an artist, matched by name. Empty when the
    /// backing Subsonic server knows no artist under that name.
    /// </summary>
    private async Task<List<Album>> GetLocalArtistAlbumsAsync(string artistName, Dictionary<string, string> parameters)
    {
        var albums = new List<Album>();

        if (string.IsNullOrWhiteSpace(artistName))
        {
            return albums;
        }

        var localArtistId = await FindLocalArtistIdAsync(artistName, parameters);
        if (string.IsNullOrEmpty(localArtistId))
        {
            return albums;
        }

        var artistParameters = BuildJsonRelayParameters(parameters);
        artistParameters["id"] = localArtistId;

        var result = await _proxyService.RelaySafeAsync("rest/getArtist", artistParameters);
        if (!result.Success || result.Body == null)
        {
            return albums;
        }

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(result.Body));
            if (doc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                response.TryGetProperty("artist", out var artistElement) &&
                artistElement.TryGetProperty("album", out var albumArray) &&
                albumArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var albumElement in albumArray.EnumerateArray())
                {
                    var album = ParseLocalAlbum(albumElement);
                    if (album != null)
                    {
                        albums.Add(album);
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse local albums of artist {ArtistName}", artistName);
        }

        return albums;
    }

    /// <summary>
    /// Looks up a local artist by name, keeping the richest one when the library holds homonyms.
    /// </summary>
    private async Task<string?> FindLocalArtistIdAsync(string artistName, Dictionary<string, string> parameters)
    {
        var searchParameters = BuildJsonRelayParameters(parameters);
        searchParameters["query"] = artistName;
        searchParameters["artistCount"] = "20";
        searchParameters["albumCount"] = "0";
        searchParameters["songCount"] = "0";

        var result = await _proxyService.RelaySafeAsync("rest/search3", searchParameters);
        if (!result.Success || result.Body == null)
        {
            return null;
        }

        var nameKey = StringNormalizer.CreateComparisonKey(artistName);

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(result.Body));
            if (!doc.RootElement.TryGetProperty("subsonic-response", out var response) ||
                !response.TryGetProperty("searchResult3", out var searchResult) ||
                !searchResult.TryGetProperty("artist", out var artistArray) ||
                artistArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestId = null;
            var bestAlbumCount = -1;

            foreach (var artistElement in artistArray.EnumerateArray())
            {
                var name = artistElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (StringNormalizer.CreateComparisonKey(name) != nameKey)
                {
                    continue;
                }

                var id = artistElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var albumCount = artistElement.TryGetProperty("albumCount", out var countElement) &&
                                 countElement.TryGetInt32(out var count)
                    ? count
                    : 0;

                if (albumCount > bestAlbumCount)
                {
                    bestId = id;
                    bestAlbumCount = albumCount;
                }
            }

            return bestId;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse local artist search for {ArtistName}", artistName);
            return null;
        }
    }

    private static Album? ParseLocalAlbum(JsonElement element)
    {
        var id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new Album
        {
            Id = id,
            Title = name,
            Artist = element.TryGetProperty("artist", out var artistElement) ? artistElement.GetString() ?? "" : "",
            ArtistId = element.TryGetProperty("artistId", out var artistIdElement) ? artistIdElement.GetString() : null,
            Year = element.TryGetProperty("year", out var yearElement) && yearElement.TryGetInt32(out var year) ? year : null,
            SongCount = element.TryGetProperty("songCount", out var countElement) && countElement.TryGetInt32(out var count) ? count : null,
            Genre = element.TryGetProperty("genre", out var genreElement) ? genreElement.GetString() : null,
            IsLocal = true
        };
    }

    /// <summary>
    /// Copies the client credentials for a server-to-server relay, dropping the parameters
    /// of the incoming request and forcing JSON so the answer can be parsed whatever
    /// format the client asked for.
    /// </summary>
    private static Dictionary<string, string> BuildJsonRelayParameters(Dictionary<string, string> parameters)
    {
        var relayParameters = new Dictionary<string, string>(parameters);
        relayParameters.Remove("id");
        relayParameters.Remove("query");
        relayParameters.Remove("artistCount");
        relayParameters.Remove("albumCount");
        relayParameters.Remove("songCount");
        relayParameters["f"] = "json";
        return relayParameters;
    }

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