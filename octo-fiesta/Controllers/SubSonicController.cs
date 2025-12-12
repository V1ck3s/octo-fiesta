using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models;
using octo_fiesta.Services;

namespace octo_fiesta.Controllers;

[ApiController]
[Route("")]
public class SubsonicController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly IMusicMetadataService _metadataService;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IDownloadService _downloadService;
    
    public SubsonicController(
        IHttpClientFactory httpClientFactory, 
        IOptions<SubsonicSettings> subsonicSettings,
        IMusicMetadataService metadataService,
        ILocalLibraryService localLibraryService,
        IDownloadService downloadService)
    {
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;
        _metadataService = metadataService;
        _localLibraryService = localLibraryService;
        _downloadService = downloadService;

        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url))
        {
            throw new Exception("Error: Environment variable SUBSONIC_URL is not set.");
        }
    }

    // Extract all parameters (query + body)
    private async Task<Dictionary<string, string>> ExtractAllParameters()
    {
        var parameters = new Dictionary<string, string>();

        // Get query parameters
        foreach (var query in Request.Query)
        {
            parameters[query.Key] = query.Value.ToString();
        }

        // Get body parameters (JSON)
        if (Request.ContentLength > 0 && Request.ContentType?.Contains("application/json") == true)
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var bodyParams = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
                    if (bodyParams != null)
                    {
                        foreach (var param in bodyParams)
                        {
                            parameters[param.Key] = param.Value?.ToString() ?? "";
                        }
                    }
                }
                catch (JsonException)
                {
                    
                }
            }
        }

        return parameters;
    }

    private async Task<(object Body, string? ContentType)> RelayToSubsonic(string endpoint, Dictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_subsonicSettings.Url}/{endpoint}?{query}";
        HttpResponseMessage response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();
        return (body, contentType);
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
                var result = await RelayToSubsonic("rest/search3", parameters);
                var contentType = result.ContentType ?? $"application/{format}";
                return File((byte[])result.Body, contentType);
            }
            catch
            {
                return CreateSubsonicResponse(format, "searchResult3", new { });
            }
        }

        var subsonicTask = RelayToSubsonicSafe("rest/search3", parameters);
        var externalTask = _metadataService.SearchAllAsync(
            cleanQuery,
            int.TryParse(parameters.GetValueOrDefault("songCount", "20"), out var sc) ? sc : 20,
            int.TryParse(parameters.GetValueOrDefault("albumCount", "20"), out var ac) ? ac : 20,
            int.TryParse(parameters.GetValueOrDefault("artistCount", "20"), out var arc) ? arc : 20
        );

        await Task.WhenAll(subsonicTask, externalTask);

        var subsonicResult = await subsonicTask;
        var externalResult = await externalTask;

        return MergeSearchResults(subsonicResult, externalResult, format);
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
            return await RelayStreamToSubsonic(parameters);
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
            return CreateSubsonicError(format, 10, "Missing id parameter");
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (!isExternal)
        {
            var result = await RelayToSubsonic("rest/getSong", parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File((byte[])result.Body, contentType);
        }

        var song = await _metadataService.GetSongAsync(provider!, externalId!);

        if (song == null)
        {
            return CreateSubsonicError(format, 70, "Song not found");
        }

        return CreateSubsonicSongResponse(format, song);
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
            return CreateSubsonicError(format, 10, "Missing id parameter");
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (isExternal)
        {
            var artist = await _metadataService.GetArtistAsync(provider!, externalId!);
            if (artist == null)
            {
                return CreateSubsonicError(format, 70, "Artist not found");
            }

            var albums = await _metadataService.GetArtistAlbumsAsync(provider!, externalId!);
            return CreateSubsonicArtistResponse(format, artist, albums);
        }

        var navidromeResult = await RelayToSubsonicSafe("rest/getArtist", parameters);
        
        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return CreateSubsonicError(format, 70, "Artist not found");
        }

        var navidromeContent = Encoding.UTF8.GetString(navidromeResult.Body);
        string artistName = "";
        var localAlbums = new List<object>();
        object? artistData = null;

        if (format == "json" || navidromeResult.ContentType?.Contains("json") == true)
        {
            var jsonDoc = JsonDocument.Parse(navidromeContent);
            if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                response.TryGetProperty("artist", out var artistElement))
            {
                artistName = artistElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                artistData = ConvertSubsonicJsonElement(artistElement, true);
                
                if (artistElement.TryGetProperty("album", out var albums))
                {
                    foreach (var album in albums.EnumerateArray())
                    {
                        localAlbums.Add(ConvertSubsonicJsonElement(album, true));
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
                mergedAlbums.Add(ConvertAlbumToSubsonicJson(deezerAlbum));
            }
        }

        if (artistData is Dictionary<string, object> artistDict)
        {
            artistDict["album"] = mergedAlbums;
            artistDict["albumCount"] = mergedAlbums.Count;
        }

        return CreateSubsonicJsonResponse(new
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
            return CreateSubsonicError(format, 10, "Missing id parameter");
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (isExternal)
        {
            var album = await _metadataService.GetAlbumAsync(provider!, externalId!);

            if (album == null)
            {
                return CreateSubsonicError(format, 70, "Album not found");
            }

            return CreateSubsonicAlbumResponse(format, album);
        }

        var navidromeResult = await RelayToSubsonicSafe("rest/getAlbum", parameters);
        
        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return CreateSubsonicError(format, 70, "Album not found");
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
                albumData = ConvertSubsonicJsonElement(albumElement, true);
                
                if (albumElement.TryGetProperty("song", out var songs))
                {
                    foreach (var song in songs.EnumerateArray())
                    {
                        localSongs.Add(ConvertSubsonicJsonElement(song, true));
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
                    mergedSongs.Add(ConvertSongToSubsonicJson(deezerSong));
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

        return CreateSubsonicJsonResponse(new
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

        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(id);

        if (!isExternal)
        {
            try
            {
                var result = await RelayToSubsonic("rest/getCoverArt", parameters);
                var contentType = result.ContentType ?? "image/jpeg";
                return File((byte[])result.Body, contentType);
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
                var artist = await _metadataService.GetArtistAsync(provider!, externalId!);
                if (artist?.ImageUrl != null)
                {
                    coverUrl = artist.ImageUrl;
                }
                break;
                
            case "album":
                var album = await _metadataService.GetAlbumAsync(provider!, externalId!);
                if (album?.CoverArtUrl != null)
                {
                    coverUrl = album.CoverArtUrl;
                }
                break;
                
            case "song":
            default:
                // For songs, try to get from song first, then album
                var song = await _metadataService.GetSongAsync(provider!, externalId!);
                if (song?.CoverArtUrl != null)
                {
                    coverUrl = song.CoverArtUrl;
                }
                else
                {
                    // Fallback: try album with same ID (legacy behavior)
                    var albumFallback = await _metadataService.GetAlbumAsync(provider!, externalId!);
                    if (albumFallback?.CoverArtUrl != null)
                    {
                        coverUrl = albumFallback.CoverArtUrl;
                    }
                }
                break;
        }
        
        if (coverUrl != null)
        {
            var response = await _httpClient.GetAsync(coverUrl);
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

    private async Task<(byte[]? Body, string? ContentType, bool Success)> RelayToSubsonicSafe(string endpoint, Dictionary<string, string> parameters)
    {
        try
        {
            var result = await RelayToSubsonic(endpoint, parameters);
            return ((byte[])result.Body, result.ContentType, true);
        }
        catch
        {
            return (null, null, false);
        }
    }

    private async Task<IActionResult> RelayStreamToSubsonic(Dictionary<string, string> parameters)
    {
        try
        {
            var query = string.Join("&", parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            var url = $"{_subsonicSettings.Url}/rest/stream?{query}";
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode);
            }

            var stream = await response.Content.ReadAsStreamAsync();
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
            
            return File(stream, contentType, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Error streaming from Subsonic: {ex.Message}" });
        }
    }

    private IActionResult MergeSearchResults(
        (byte[]? Body, string? ContentType, bool Success) subsonicResult,
        SearchResult externalResult,
        string format)
    {
        var localSongs = new List<object>();
        var localAlbums = new List<object>();
        var localArtists = new List<object>();

        if (subsonicResult.Success && subsonicResult.Body != null)
        {
            try
            {
                var subsonicContent = Encoding.UTF8.GetString(subsonicResult.Body);
                
                if (format == "json" || subsonicResult.ContentType?.Contains("json") == true)
                {
                    var jsonDoc = JsonDocument.Parse(subsonicContent);
                    if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                        response.TryGetProperty("searchResult3", out var searchResult))
                    {
                        if (searchResult.TryGetProperty("song", out var songs))
                        {
                            foreach (var song in songs.EnumerateArray())
                            {
                                localSongs.Add(ConvertSubsonicJsonElement(song, true));
                            }
                        }
                        if (searchResult.TryGetProperty("album", out var albums))
                        {
                            foreach (var album in albums.EnumerateArray())
                            {
                                localAlbums.Add(ConvertSubsonicJsonElement(album, true));
                            }
                        }
                        if (searchResult.TryGetProperty("artist", out var artists))
                        {
                            foreach (var artist in artists.EnumerateArray())
                            {
                                localArtists.Add(ConvertSubsonicJsonElement(artist, true));
                            }
                        }
                    }
                }
                else
                {
                    var xmlDoc = XDocument.Parse(subsonicContent);
                    var ns = xmlDoc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                    var searchResult = xmlDoc.Descendants(ns + "searchResult3").FirstOrDefault();
                    
                    if (searchResult != null)
                    {
                        foreach (var song in searchResult.Elements(ns + "song"))
                        {
                            localSongs.Add(ConvertSubsonicXmlElement(song, "song"));
                        }
                        foreach (var album in searchResult.Elements(ns + "album"))
                        {
                            localAlbums.Add(ConvertSubsonicXmlElement(album, "album"));
                        }
                        foreach (var artist in searchResult.Elements(ns + "artist"))
                        {
                            localArtists.Add(ConvertSubsonicXmlElement(artist, "artist"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing Subsonic response: {ex.Message}");
            }
        }

        if (format == "json")
        {
            var mergedSongs = localSongs
                .Concat(externalResult.Songs.Select(s => ConvertSongToSubsonicJson(s)))
                .ToList();
            var mergedAlbums = localAlbums
                .Concat(externalResult.Albums.Select(a => ConvertAlbumToSubsonicJson(a)))
                .ToList();
            
            // Deduplicate artists by name - prefer local artists over external ones
            var localArtistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artist in localArtists)
            {
                if (artist is Dictionary<string, object> dict && dict.TryGetValue("name", out var nameObj))
                {
                    localArtistNames.Add(nameObj?.ToString() ?? "");
                }
            }
            
            var mergedArtists = localArtists.ToList();
            foreach (var externalArtist in externalResult.Artists)
            {
                // Only add external artist if no local artist with same name exists
                if (!localArtistNames.Contains(externalArtist.Name))
                {
                    mergedArtists.Add(ConvertArtistToSubsonicJson(externalArtist));
                }
            }

            return CreateSubsonicJsonResponse(new
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
            
            // Deduplicate artists by name - prefer local artists over external ones
            var localArtistNamesXml = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artist in localArtists.Cast<XElement>())
            {
                var name = artist.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    localArtistNamesXml.Add(name);
                }
                artist.Name = ns + "artist";
                searchResult3.Add(artist);
            }
            foreach (var artist in externalResult.Artists)
            {
                // Only add external artist if no local artist with same name exists
                if (!localArtistNamesXml.Contains(artist.Name))
                {
                    searchResult3.Add(ConvertArtistToSubsonicXml(artist, ns));
                }
            }
            
            foreach (var album in localAlbums.Cast<XElement>())
            {
                album.Name = ns + "album";
                searchResult3.Add(album);
            }
            foreach (var album in externalResult.Albums)
            {
                searchResult3.Add(ConvertAlbumToSubsonicXml(album, ns));
            }
            
            foreach (var song in localSongs.Cast<XElement>())
            {
                song.Name = ns + "song";
                searchResult3.Add(song);
            }
            foreach (var song in externalResult.Songs)
            {
                searchResult3.Add(ConvertSongToSubsonicXml(song, ns));
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

    private object ConvertSubsonicJsonElement(JsonElement element, bool isLocal)
    {
        var dict = new Dictionary<string, object>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonValue(prop.Value);
        }
        dict["isExternal"] = !isLocal;
        return dict;
    }

    private object ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToList(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonValue(p.Value)),
            JsonValueKind.Null => null!,
            _ => value.ToString()
        };
    }

    private XElement ConvertSubsonicXmlElement(XElement element, string type)
    {
        var newElement = new XElement(element);
        newElement.SetAttributeValue("isExternal", "false");
        return newElement;
    }

    private object ConvertSongToSubsonicJson(Song song)
    {
        return new
        {
            id = song.Id,
            parent = song.AlbumId ?? "",
            isDir = false,
            title = song.Title,
            album = song.Album,
            artist = song.Artist,
            albumId = song.AlbumId,
            artistId = song.ArtistId,
            duration = song.Duration ?? 0,
            track = song.Track ?? 0,
            year = song.Year ?? 0,
            coverArt = song.Id,
            suffix = song.IsLocal ? "mp3" : "Remote",
            bitRate = song.IsLocal ? (int?)null : 0,
            contentType = "audio/mpeg",
            type = "music",
            isVideo = false,
            isExternal = !song.IsLocal
        };
    }

    private object ConvertAlbumToSubsonicJson(Album album)
    {
        return new
        {
            id = album.Id,
            name = album.Title,
            artist = album.Artist,
            artistId = album.ArtistId,
            songCount = album.SongCount ?? 0,
            year = album.Year ?? 0,
            coverArt = album.Id,
            isExternal = !album.IsLocal
        };
    }

    private object ConvertArtistToSubsonicJson(Artist artist)
    {
        return new
        {
            id = artist.Id,
            name = artist.Name,
            albumCount = artist.AlbumCount ?? 0,
            coverArt = artist.Id,
            isExternal = !artist.IsLocal
        };
    }

    private XElement ConvertSongToSubsonicXml(Song song, XNamespace ns)
    {
        return new XElement(ns + "song",
            new XAttribute("id", song.Id),
            new XAttribute("title", song.Title),
            new XAttribute("album", song.Album ?? ""),
            new XAttribute("artist", song.Artist ?? ""),
            new XAttribute("duration", song.Duration ?? 0),
            new XAttribute("track", song.Track ?? 0),
            new XAttribute("year", song.Year ?? 0),
            new XAttribute("coverArt", song.Id),
            new XAttribute("isExternal", (!song.IsLocal).ToString().ToLower())
        );
    }

    private XElement ConvertAlbumToSubsonicXml(Album album, XNamespace ns)
    {
        return new XElement(ns + "album",
            new XAttribute("id", album.Id),
            new XAttribute("name", album.Title),
            new XAttribute("artist", album.Artist ?? ""),
            new XAttribute("songCount", album.SongCount ?? 0),
            new XAttribute("year", album.Year ?? 0),
            new XAttribute("coverArt", album.Id),
            new XAttribute("isExternal", (!album.IsLocal).ToString().ToLower())
        );
    }

    private XElement ConvertArtistToSubsonicXml(Artist artist, XNamespace ns)
    {
        return new XElement(ns + "artist",
            new XAttribute("id", artist.Id),
            new XAttribute("name", artist.Name),
            new XAttribute("albumCount", artist.AlbumCount ?? 0),
            new XAttribute("coverArt", artist.Id),
            new XAttribute("isExternal", (!artist.IsLocal).ToString().ToLower())
        );
    }

    /// <summary>
    /// Creates a JSON Subsonic response with "subsonic-response" key (with hyphen).
    /// </summary>
    private IActionResult CreateSubsonicJsonResponse(object responseContent)
    {
        var response = new Dictionary<string, object>
        {
            ["subsonic-response"] = responseContent
        };
        return new JsonResult(response);
    }

    private IActionResult CreateSubsonicResponse(string format, string elementName, object data)
    {
        if (format == "json")
        {
            return CreateSubsonicJsonResponse(new { status = "ok", version = "1.16.1" });
        }
        
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", "1.16.1"),
                new XElement(ns + elementName)
            )
        );
        return Content(doc.ToString(), "application/xml");
    }

    private IActionResult CreateSubsonicError(string format, int code, string message)
    {
        if (format == "json")
        {
            return CreateSubsonicJsonResponse(new 
            { 
                status = "failed", 
                version = "1.16.1",
                error = new { code, message }
            });
        }
        
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "failed"),
                new XAttribute("version", "1.16.1"),
                new XElement(ns + "error",
                    new XAttribute("code", code),
                    new XAttribute("message", message)
                )
            )
        );
        return Content(doc.ToString(), "application/xml");
    }

    private IActionResult CreateSubsonicSongResponse(string format, Song song)
    {
        if (format == "json")
        {
            return CreateSubsonicJsonResponse(new 
            { 
                status = "ok", 
                version = "1.16.1",
                song = ConvertSongToSubsonicJson(song)
            });
        }
        
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", "1.16.1"),
                ConvertSongToSubsonicXml(song, ns)
            )
        );
        return Content(doc.ToString(), "application/xml");
    }

    private IActionResult CreateSubsonicAlbumResponse(string format, Album album)
    {
        // Calculate total duration from songs
        var totalDuration = album.Songs.Sum(s => s.Duration ?? 0);
        
        if (format == "json")
        {
            return CreateSubsonicJsonResponse(new 
            { 
                status = "ok", 
                version = "1.16.1",
                album = new
                {
                    id = album.Id,
                    name = album.Title,
                    artist = album.Artist,
                    artistId = album.ArtistId,
                    coverArt = album.Id,
                    songCount = album.Songs.Count > 0 ? album.Songs.Count : (album.SongCount ?? 0),
                    duration = totalDuration,
                    year = album.Year ?? 0,
                    genre = album.Genre ?? "",
                    isCompilation = false,
                    song = album.Songs.Select(s => ConvertSongToSubsonicJson(s)).ToList()
                }
            });
        }
        
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", "1.16.1"),
                new XElement(ns + "album",
                    new XAttribute("id", album.Id),
                    new XAttribute("name", album.Title),
                    new XAttribute("artist", album.Artist ?? ""),
                    new XAttribute("songCount", album.SongCount ?? 0),
                    new XAttribute("year", album.Year ?? 0),
                    new XAttribute("coverArt", album.Id),
                    album.Songs.Select(s => ConvertSongToSubsonicXml(s, ns))
                )
            )
        );
        return Content(doc.ToString(), "application/xml");
    }

    private IActionResult CreateSubsonicArtistResponse(string format, Artist artist, List<Album> albums)
    {
        if (format == "json")
        {
            return CreateSubsonicJsonResponse(new 
            { 
                status = "ok", 
                version = "1.16.1",
                artist = new
                {
                    id = artist.Id,
                    name = artist.Name,
                    coverArt = artist.Id,
                    albumCount = albums.Count,
                    artistImageUrl = artist.ImageUrl,
                    album = albums.Select(a => ConvertAlbumToSubsonicJson(a)).ToList()
                }
            });
        }
        
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", "1.16.1"),
                new XElement(ns + "artist",
                    new XAttribute("id", artist.Id),
                    new XAttribute("name", artist.Name),
                    new XAttribute("coverArt", artist.Id),
                    new XAttribute("albumCount", albums.Count),
                    albums.Select(a => ConvertAlbumToSubsonicXml(a, ns))
                )
            )
        );
        return Content(doc.ToString(), "application/xml");
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

    // Generic endpoint to handle all subsonic API calls
    [HttpGet, HttpPost]
    [Route("{**endpoint}")]
    public async Task<IActionResult> GenericEndpoint(string endpoint)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        
        try
        {
            var result = await RelayToSubsonic(endpoint, parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File((byte[])result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            // Return Subsonic-compatible error response
            return CreateSubsonicError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
        }
    }
}