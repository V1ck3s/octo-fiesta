using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;
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

    [HttpGet, HttpPost]
    [Route("ping")]
    public async Task<IActionResult> Ping()
    {
        var parameters = await ExtractAllParameters();
        var xml = await RelayToSubsonic("ping", parameters);
        var doc = XDocument.Parse((string)xml.Body);
        var status = doc.Root?.Attribute("status")?.Value;
        return Ok(new { status });
    }

    /// <summary>
    /// Endpoint search3 personnalisé - fusionne les résultats locaux et externes
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/search3")]
    [Route("rest/search3.view")]
    public async Task<IActionResult> Search3()
    {
        var parameters = await ExtractAllParameters();
        var query = parameters.GetValueOrDefault("query", "");
        var format = parameters.GetValueOrDefault("f", "xml");
        
        if (string.IsNullOrWhiteSpace(query))
        {
            return CreateSubsonicResponse(format, "searchResult3", new { });
        }

        // Lancer les deux recherches en parallèle
        var subsonicTask = RelayToSubsonicSafe("rest/search3", parameters);
        var externalTask = _metadataService.SearchAllAsync(
            query,
            int.TryParse(parameters.GetValueOrDefault("songCount", "20"), out var sc) ? sc : 20,
            int.TryParse(parameters.GetValueOrDefault("albumCount", "20"), out var ac) ? ac : 20,
            int.TryParse(parameters.GetValueOrDefault("artistCount", "20"), out var arc) ? arc : 20
        );

        await Task.WhenAll(subsonicTask, externalTask);

        var subsonicResult = await subsonicTask;
        var externalResult = await externalTask;

        // Fusionner les résultats
        return MergeSearchResults(subsonicResult, externalResult, format);
    }

    /// <summary>
    /// Endpoint stream personnalisé - télécharge à la volée si nécessaire
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
            // Chanson locale - relayer vers Subsonic
            return await RelayStreamToSubsonic(parameters);
        }

        // Chanson externe - vérifier si déjà téléchargée
        var localPath = await _localLibraryService.GetLocalPathForExternalSongAsync(provider!, externalId!);

        if (localPath != null && System.IO.File.Exists(localPath))
        {
            // Fichier déjà disponible localement
            var stream = System.IO.File.OpenRead(localPath);
            return File(stream, GetContentType(localPath), enableRangeProcessing: true);
        }

        // Télécharger et streamer à la volée
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
    /// Endpoint getSong personnalisé - retourne les infos d'une chanson externe si nécessaire
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
            // Chanson locale - relayer vers Subsonic
            var result = await RelayToSubsonic("rest/getSong", parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File((byte[])result.Body, contentType);
        }

        // Chanson externe - récupérer depuis le service de métadonnées
        var song = await _metadataService.GetSongAsync(provider!, externalId!);

        if (song == null)
        {
            return CreateSubsonicError(format, 70, "Song not found");
        }

        return CreateSubsonicSongResponse(format, song);
    }

    /// <summary>
    /// Endpoint getAlbum personnalisé
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

        if (!isExternal)
        {
            // Album local - relayer vers Subsonic
            var result = await RelayToSubsonic("rest/getAlbum", parameters);
            var contentType = result.ContentType ?? $"application/{format}";
            return File((byte[])result.Body, contentType);
        }

        // Album externe - récupérer depuis le service de métadonnées
        var album = await _metadataService.GetAlbumAsync(provider!, externalId!);

        if (album == null)
        {
            return CreateSubsonicError(format, 70, "Album not found");
        }

        return CreateSubsonicAlbumResponse(format, album);
    }

    /// <summary>
    /// Endpoint getCoverArt personnalisé - proxy les covers externes
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

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (!isExternal)
        {
            // Cover local - relayer vers Subsonic
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

        // Cover externe - récupérer l'URL depuis les métadonnées
        var song = await _metadataService.GetSongAsync(provider!, externalId!);
        if (song?.CoverArtUrl != null)
        {
            // Proxy l'image
            var response = await _httpClient.GetAsync(song.CoverArtUrl);
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
        // Créer la réponse fusionnée au format Subsonic
        if (format == "json")
        {
            var response = new
            {
                subsonicResponse = new
                {
                    status = "ok",
                    version = "1.16.1",
                    searchResult3 = new
                    {
                        song = externalResult.Songs.Select(s => ConvertSongToSubsonicJson(s)).ToList(),
                        album = externalResult.Albums.Select(a => ConvertAlbumToSubsonicJson(a)).ToList(),
                        artist = externalResult.Artists.Select(a => ConvertArtistToSubsonicJson(a)).ToList()
                    }
                }
            };

            // TODO: Fusionner avec les résultats Subsonic si disponibles
            
            return Ok(response);
        }
        else
        {
            // Format XML
            var ns = XNamespace.Get("http://subsonic.org/restapi");
            var doc = new XDocument(
                new XElement(ns + "subsonic-response",
                    new XAttribute("status", "ok"),
                    new XAttribute("version", "1.16.1"),
                    new XElement(ns + "searchResult3",
                        externalResult.Artists.Select(a => ConvertArtistToSubsonicXml(a, ns)),
                        externalResult.Albums.Select(a => ConvertAlbumToSubsonicXml(a, ns)),
                        externalResult.Songs.Select(s => ConvertSongToSubsonicXml(s, ns))
                    )
                )
            );

            return Content(doc.ToString(), "application/xml");
        }
    }

    private object ConvertSongToSubsonicJson(Song song)
    {
        return new
        {
            id = song.Id,
            title = song.Title,
            album = song.Album,
            artist = song.Artist,
            albumId = song.AlbumId,
            artistId = song.ArtistId,
            duration = song.Duration ?? 0,
            track = song.Track ?? 0,
            year = song.Year ?? 0,
            coverArt = song.Id, // Utilisé pour getCoverArt
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

    private IActionResult CreateSubsonicResponse(string format, string elementName, object data)
    {
        if (format == "json")
        {
            return Ok(new { subsonicResponse = new { status = "ok", version = "1.16.1" } });
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
            return Ok(new 
            { 
                subsonicResponse = new 
                { 
                    status = "failed", 
                    version = "1.16.1",
                    error = new { code, message }
                } 
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
            return Ok(new 
            { 
                subsonicResponse = new 
                { 
                    status = "ok", 
                    version = "1.16.1",
                    song = ConvertSongToSubsonicJson(song)
                } 
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
        if (format == "json")
        {
            return Ok(new 
            { 
                subsonicResponse = new 
                { 
                    status = "ok", 
                    version = "1.16.1",
                    album = new
                    {
                        id = album.Id,
                        name = album.Title,
                        artist = album.Artist,
                        artistId = album.ArtistId,
                        songCount = album.SongCount ?? 0,
                        year = album.Year ?? 0,
                        coverArt = album.Id,
                        song = album.Songs.Select(s => ConvertSongToSubsonicJson(s)).ToList()
                    }
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
        try
        {
            var result = await RelayToSubsonic(endpoint, parameters);
            var contentType = result.ContentType ?? $"application/{parameters.GetValueOrDefault("f", "xml")}";
            return File((byte[])result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            return BadRequest(new { error = $"Error while calling Subsonic: {ex.Message}" });
        }
    }
}