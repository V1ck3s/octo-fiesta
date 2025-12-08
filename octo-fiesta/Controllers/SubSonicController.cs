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
        
        // Nettoyer la query (enlever les guillemets vides)
        var cleanQuery = query.Trim().Trim('"');
        
        // Si la query est vide, relayer directement vers Subsonic (browse all songs)
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

        // Lancer les deux recherches en parallèle
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
    /// Endpoint getArtist personnalisé - fusionne les albums locaux et Deezer
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
            // Artiste externe - récupérer depuis Deezer avec ses albums
            var artist = await _metadataService.GetArtistAsync(provider!, externalId!);
            if (artist == null)
            {
                return CreateSubsonicError(format, 70, "Artist not found");
            }

            var albums = await _metadataService.GetArtistAlbumsAsync(provider!, externalId!);
            return CreateSubsonicArtistResponse(format, artist, albums);
        }

        // Artiste local - récupérer depuis Navidrome puis enrichir avec Deezer
        var navidromeResult = await RelayToSubsonicSafe("rest/getArtist", parameters);
        
        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return CreateSubsonicError(format, 70, "Artist not found");
        }

        // Parser la réponse Navidrome pour extraire le nom de l'artiste et les albums locaux
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
                
                // Convertir les données de l'artiste
                artistData = ConvertSubsonicJsonElement(artistElement, true);
                
                // Extraire les albums locaux
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
            // Retourner la réponse Navidrome telle quelle si on ne peut pas parser
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/json");
        }

        // Chercher l'artiste sur Deezer pour obtenir ses albums
        var deezerArtists = await _metadataService.SearchArtistsAsync(artistName, 1);
        var deezerAlbums = new List<Album>();
        
        if (deezerArtists.Count > 0)
        {
            var deezerArtist = deezerArtists[0];
            // Vérifier que c'est bien le même artiste (nom similaire)
            if (deezerArtist.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            {
                deezerAlbums = await _metadataService.GetArtistAlbumsAsync("deezer", deezerArtist.ExternalId!);
            }
        }

        // Fusionner: albums locaux en premier, puis albums Deezer non présents localement
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
            // Ne pas ajouter si un album avec le même nom existe déjà localement
            if (!localAlbumNames.Contains(deezerAlbum.Title))
            {
                mergedAlbums.Add(ConvertAlbumToSubsonicJson(deezerAlbum));
            }
        }

        // Construire la réponse avec les albums fusionnés
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
    /// Endpoint getAlbum personnalisé - enrichit avec les chansons Deezer si nécessaire
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

        // Cover externe - essayer track, album, puis artist
        string? coverUrl = null;
        
        // Essayer en tant que track
        var song = await _metadataService.GetSongAsync(provider!, externalId!);
        if (song?.CoverArtUrl != null)
        {
            coverUrl = song.CoverArtUrl;
        }
        
        // Si pas trouvé, essayer en tant qu'album
        if (coverUrl == null)
        {
            var album = await _metadataService.GetAlbumAsync(provider!, externalId!);
            if (album?.CoverArtUrl != null)
            {
                coverUrl = album.CoverArtUrl;
            }
        }
        
        // Si pas trouvé, essayer en tant qu'artiste
        if (coverUrl == null)
        {
            var artist = await _metadataService.GetArtistAsync(provider!, externalId!);
            if (artist?.ImageUrl != null)
            {
                coverUrl = artist.ImageUrl;
            }
        }
        
        if (coverUrl != null)
        {
            // Proxy l'image
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
        // Parser les résultats Subsonic si disponibles
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
                    // Parser JSON Subsonic
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
                    // Parser XML Subsonic
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
                // Log l'erreur mais continue avec les résultats externes
                Console.WriteLine($"Error parsing Subsonic response: {ex.Message}");
            }
        }

        // Fusionner: résultats locaux en premier, puis externes
        if (format == "json")
        {
            var mergedSongs = localSongs
                .Concat(externalResult.Songs.Select(s => ConvertSongToSubsonicJson(s)))
                .ToList();
            var mergedAlbums = localAlbums
                .Concat(externalResult.Albums.Select(a => ConvertAlbumToSubsonicJson(a)))
                .ToList();
            var mergedArtists = localArtists
                .Concat(externalResult.Artists.Select(a => ConvertArtistToSubsonicJson(a)))
                .ToList();

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
            // Format XML
            var ns = XNamespace.Get("http://subsonic.org/restapi");
            
            var searchResult3 = new XElement(ns + "searchResult3");
            
            // Ajouter les artistes locaux puis externes
            foreach (var artist in localArtists.Cast<XElement>())
            {
                artist.Name = ns + "artist";
                searchResult3.Add(artist);
            }
            foreach (var artist in externalResult.Artists)
            {
                searchResult3.Add(ConvertArtistToSubsonicXml(artist, ns));
            }
            
            // Ajouter les albums locaux puis externes
            foreach (var album in localAlbums.Cast<XElement>())
            {
                album.Name = ns + "album";
                searchResult3.Add(album);
            }
            foreach (var album in externalResult.Albums)
            {
                searchResult3.Add(ConvertAlbumToSubsonicXml(album, ns));
            }
            
            // Ajouter les chansons locales puis externes
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
            coverArt = song.Id, // Utilisé pour getCoverArt
            suffix = "mp3",
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
    /// Crée une réponse JSON Subsonic avec la clé "subsonic-response" (avec tiret)
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