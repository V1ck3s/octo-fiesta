using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace octo_fiesta.Services.Deezer;

/// <summary>
/// Metadata service implementation using the Deezer API (free, no key required)
/// </summary>
public class DeezerMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _settings;
    private readonly string? _arl;
    private readonly ILogger<DeezerMetadataService> _logger;
    private const string BaseUrl = "https://api.deezer.com";

    // Deezer's private gateway (the same one the downloader uses). Unlike the
    // public api.deezer.com, it is NOT filtered by the server's outbound IP
    // country, so search returns the full catalog even from regions where Deezer
    // is closed (e.g. RU/BY). See issue #232.
    private const string GatewayUrl = "https://www.deezer.com/ajax/gw-light.php";
    private const string ImageCdnUrl = "https://e-cdns-images.dzcdn.net/images";
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _apiToken;
    private DateTime _apiTokenExpiry = DateTime.MinValue;

    public DeezerMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<SubsonicSettings> settings,
        IOptions<DeezerSettings> deezerSettings,
        ILogger<DeezerMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _arl = deezerSettings.Value.Arl;
        _logger = logger;
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        try
        {
            var url = $"{BaseUrl}/search/track?q={Uri.EscapeDataString(query)}&limit={limit}";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode) return new List<Song>();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            var songs = new List<Song>();
            if (result.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var track in data.EnumerateArray())
                {
                    var song = ParseDeezerTrack(track);
                    if (ShouldIncludeSong(song))
                    {
                        songs.Add(song);
                    }
                }
            }
            
            return songs;
        }
        catch
        {
            return new List<Song>();
        }
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        try
        {
            var url = $"{BaseUrl}/search/album?q={Uri.EscapeDataString(query)}&limit={limit}";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode) return new List<Album>();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            var albums = new List<Album>();
            if (result.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var album in data.EnumerateArray())
                {
                    albums.Add(ParseDeezerAlbum(album));
                }
            }
            
            return albums;
        }
        catch
        {
            return new List<Album>();
        }
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        try
        {
            var url = $"{BaseUrl}/search/artist?q={Uri.EscapeDataString(query)}&limit={limit}";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode) return new List<Artist>();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            var artists = new List<Artist>();
            if (result.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var artist in data.EnumerateArray())
                {
                    artists.Add(ParseDeezerArtist(artist));
                }
            }
            
            return artists;
        }
        catch
        {
            return new List<Artist>();
        }
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        // Prefer the private gateway: the public API is filtered by the server's
        // IP country and returns truncated results from regions where Deezer is
        // closed. The gateway uses the account/session instead. See issue #232.
        var privateResult = await SearchViaGatewayAsync(query, songLimit, albumLimit, artistLimit);
        if (privateResult != null)
        {
            await EnrichWithArtistDiscographyAsync(privateResult, query);
            return privateResult;
        }

        // Execute searches in parallel
        var songsTask = SearchSongsAsync(query, songLimit);
        var albumsTask = SearchAlbumsAsync(query, albumLimit);
        var artistsTask = SearchArtistsAsync(query, artistLimit);

        await Task.WhenAll(songsTask, albumsTask, artistsTask);

        var result = new SearchResult
        {
            Songs = await songsTask,
            Albums = await albumsTask,
            Artists = await artistsTask
        };
        await EnrichWithArtistDiscographyAsync(result, query);
        return result;
    }

    /// <summary>
    /// Deezer search ranks albums by popularity, so recent releases get pushed past
    /// the limit. When the query exactly matches an artist, merge that artist's full
    /// discography into the album results so latest releases surface (issue #232).
    /// Applies to both the gateway and public-API search paths.
    /// </summary>
    private async Task EnrichWithArtistDiscographyAsync(SearchResult result, string query)
    {
        var matchedArtist = result.Artists.FirstOrDefault(a =>
            string.Equals(a.Name, query, StringComparison.OrdinalIgnoreCase));
        if (matchedArtist == null || string.IsNullOrEmpty(matchedArtist.ExternalId)) return;

        var discography = await GetArtistAlbumsAsync("deezer", matchedArtist.ExternalId);
        foreach (var a in discography)
        {
            if (string.IsNullOrEmpty(a.Artist)) a.Artist = matchedArtist.Name;
            if (string.IsNullOrEmpty(a.ArtistId)) a.ArtistId = matchedArtist.Id;
        }

        var seen = new HashSet<string>(result.Albums.Select(a => a.ExternalId ?? a.Id));
        foreach (var a in discography)
        {
            if (seen.Add(a.ExternalId ?? a.Id)) result.Albums.Add(a);
        }
    }

    #region Private gateway search (issue #232)

    /// <summary>
    /// Searches via Deezer's private gateway (deezer.pageSearch). Returns null on
    /// any failure so the caller can fall back to the public API.
    /// </summary>
    private async Task<SearchResult?> SearchViaGatewayAsync(string query, int songLimit, int albumLimit, int artistLimit)
    {
        try
        {
            var nb = Math.Max(1, Math.Max(songLimit, Math.Max(albumLimit, artistLimit)));
            var results = await CallGatewayWithRetryAsync(
                "deezer.pageSearch",
                new { query, start = 0, nb, top_tracks = true });
            if (results == null) return null;

            var resultsEl = results.Value;
            var songs = ParseGatewaySection(resultsEl, "TRACK", songLimit, ParseGatewayTrack);
            var albums = ParseGatewaySection(resultsEl, "ALBUM", albumLimit, ParseGatewayAlbum);
            var artists = ParseGatewaySection(resultsEl, "ARTIST", artistLimit, ParseGatewayArtist);

            songs = songs.Where(ShouldIncludeSong).ToList();

            return new SearchResult { Songs = songs, Albums = albums, Artists = artists };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deezer gateway search failed for '{Query}', falling back to public API", query);
            return null;
        }
    }

    /// <summary>
    /// Calls a gateway method, retrying once with a fresh token if the first call
    /// fails (the cached token may have expired). Returns null on any failure so
    /// callers can fall back to the public API.
    /// </summary>
    private async Task<JsonElement?> CallGatewayWithRetryAsync(string method, object payload)
    {
        var token = await EnsureApiTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        var results = await CallGatewayMethodAsync(method, payload, token);
        if (results == null)
        {
            _apiTokenExpiry = DateTime.MinValue;
            token = await EnsureApiTokenAsync();
            if (string.IsNullOrEmpty(token)) return null;
            results = await CallGatewayMethodAsync(method, payload, token);
        }
        return results;
    }

    private async Task<JsonElement?> CallGatewayMethodAsync(string method, object payload, string token)
    {
        var url = $"{GatewayUrl}?method={method}&input=3&api_version=1.0&api_token={Uri.EscapeDataString(token)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(_arl))
        {
            request.Headers.Add("Cookie", $"arl={_arl}");
        }
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // A non-empty error (e.g. expired token) means we should retry/fall back.
        if (root.TryGetProperty("error", out var error) &&
            (error.ValueKind != JsonValueKind.Array || error.GetArrayLength() > 0) &&
            (error.ValueKind != JsonValueKind.Object || error.EnumerateObject().Any()))
        {
            return null;
        }

        if (!root.TryGetProperty("results", out var results)) return null;

        // Clone so the JsonElement stays valid after the JsonDocument is disposed.
        return results.Clone();
    }

    /// <summary>
    /// Fetches an artist's full discography via the private gateway
    /// (album.getDiscography), bypassing the public API's IP geo-filtering.
    /// Returns null on any failure so the caller can fall back to the public API.
    /// </summary>
    private async Task<List<Album>?> GetArtistAlbumsViaGatewayAsync(string artistId)
    {
        try
        {
            var results = await CallGatewayWithRetryAsync(
                "album.getDiscography",
                new { art_id = artistId, discography_mode = "all", nb = 500, nb_songs = 0, start = 0 });
            if (results == null) return null;

            if (!results.Value.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var albums = new List<Album>();
            foreach (var a in data.EnumerateArray())
            {
                albums.Add(ParseGatewayAlbum(a));
            }
            return albums;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deezer gateway discography failed for artist '{ArtistId}', falling back to public API", artistId);
            return null;
        }
    }

    /// <summary>
    /// Obtains and caches a gateway api_token. Uses the configured ARL when present
    /// (account session), otherwise an anonymous guest token — both bypass the
    /// public API's IP geo-filtering.
    /// </summary>
    private async Task<string?> EnsureApiTokenAsync()
    {
        if (!string.IsNullOrEmpty(_apiToken) && DateTime.UtcNow < _apiTokenExpiry)
        {
            return _apiToken;
        }

        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_apiToken) && DateTime.UtcNow < _apiTokenExpiry)
            {
                return _apiToken;
            }

            var url = $"{GatewayUrl}?method=deezer.getUserData&input=3&api_version=1.0&api_token=null";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(_arl))
            {
                request.Headers.Add("Cookie", $"arl={_arl}");
            }
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("results", out var results) &&
                results.TryGetProperty("checkForm", out var checkForm))
            {
                _apiToken = checkForm.GetString();
                _apiTokenExpiry = DateTime.UtcNow.AddMinutes(50);
                return _apiToken;
            }

            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static List<T> ParseGatewaySection<T>(JsonElement results, string section, int limit, Func<JsonElement, T> parse)
    {
        var items = new List<T>();
        if (limit <= 0) return items;
        if (results.TryGetProperty(section, out var sectionEl) &&
            sectionEl.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                items.Add(parse(item));
                if (items.Count >= limit) break;
            }
        }
        return items;
    }

    private Song ParseGatewayTrack(JsonElement t)
    {
        var sngId = GwString(t, "SNG_ID") ?? "";
        var albId = GwString(t, "ALB_ID");
        var artId = GwString(t, "ART_ID");
        var albPicture = GwString(t, "ALB_PICTURE");

        return new Song
        {
            Title = GwString(t, "SNG_TITLE") ?? "",
            Artist = GwString(t, "ART_NAME") ?? "",
            Artists = ParseGatewayArtistList(t),
            ArtistId = artId != null ? $"ext-deezer-artist-{artId}" : null,
            Album = GwString(t, "ALB_TITLE") ?? "",
            AlbumId = albId != null ? $"ext-deezer-album-{albId}" : null,
            Duration = GwInt(t, "DURATION"),
            Track = GwInt(t, "TRACK_NUMBER"),
            DiscNumber = GwInt(t, "DISK_NUMBER"),
            Isrc = GwString(t, "ISRC"),
            CoverArtUrl = BuildImageUrl("cover", albPicture, 250),
            CoverArtUrlLarge = BuildImageUrl("cover", albPicture, 1000),
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = sngId,
            ExplicitContentLyrics = GwInt(t, "EXPLICIT_LYRICS")
        };
    }

    private Album ParseGatewayAlbum(JsonElement a)
    {
        var albId = GwString(a, "ALB_ID") ?? "";
        var artId = GwString(a, "ART_ID");
        var picture = GwString(a, "ALB_PICTURE");
        var releaseDate = GwString(a, "ORIGINAL_RELEASE_DATE")
            ?? GwString(a, "PHYSICAL_RELEASE_DATE")
            ?? GwString(a, "DIGITAL_RELEASE_DATE");

        return new Album
        {
            Id = $"ext-deezer-album-{albId}",
            Title = GwString(a, "ALB_TITLE") ?? "",
            Artist = GwString(a, "ART_NAME") ?? "",
            ArtistId = artId != null ? $"ext-deezer-artist-{artId}" : null,
            Year = ParseYear(releaseDate),
            SongCount = GwInt(a, "NUMBER_TRACK"),
            CoverArtUrl = BuildImageUrl("cover", picture, 250),
            CoverArtUrlLarge = BuildImageUrl("cover", picture, 1000),
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = albId
        };
    }

    private Artist ParseGatewayArtist(JsonElement a)
    {
        var artId = GwString(a, "ART_ID") ?? "";
        var picture = GwString(a, "ART_PICTURE");

        return new Artist
        {
            Id = $"ext-deezer-artist-{artId}",
            Name = GwString(a, "ART_NAME") ?? "",
            ImageUrl = BuildImageUrl("artist", picture, 250),
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = artId
        };
    }

    private List<Artist> ParseGatewayArtistList(JsonElement t)
    {
        var list = new List<Artist>();
        if (t.TryGetProperty("ARTISTS", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artists.EnumerateArray())
            {
                list.Add(ParseGatewayArtist(a));
            }
        }

        if (list.Count == 0)
        {
            var artId = GwString(t, "ART_ID");
            if (artId != null)
            {
                list.Add(new Artist
                {
                    Id = $"ext-deezer-artist-{artId}",
                    Name = GwString(t, "ART_NAME") ?? "",
                    IsLocal = false,
                    ExternalProvider = "deezer",
                    ExternalId = artId
                });
            }
        }

        return list;
    }

    private static string? BuildImageUrl(string type, string? md5, int size)
    {
        if (string.IsNullOrEmpty(md5)) return null;
        return $"{ImageCdnUrl}/{type}/{md5}/{size}x{size}-000000-80-0-0.jpg";
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrEmpty(date)) return null;
        return int.TryParse(date.Split('-')[0], out var year) && year > 0 ? year : null;
    }

    // Gateway fields are inconsistently typed (numbers arrive as strings).
    private static string? GwString(JsonElement t, string name)
    {
        if (!t.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null
        };
    }

    private static int? GwInt(JsonElement t, string name)
    {
        if (!t.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    #endregion

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "deezer") return null;
        
        var url = $"{BaseUrl}/track/{externalId}";
        var response = await _httpClient.GetAsync(url);
        
        if (!response.IsSuccessStatusCode) return null;
        
        var json = await response.Content.ReadAsStringAsync();
        var track = JsonDocument.Parse(json).RootElement;
        
        if (track.TryGetProperty("error", out _)) return null;
        
        // For an individual track, get full metadata
        var song = ParseDeezerTrackFull(track);
        
        // Get additional info from album (genre, total track count, label, copyright)
        if (track.TryGetProperty("album", out var albumRef) &&
            albumRef.TryGetProperty("id", out var albumIdEl))
        {
            var albumId = albumIdEl.GetInt64().ToString();
            try
            {
                var albumUrl = $"{BaseUrl}/album/{albumId}";
                var albumResponse = await _httpClient.GetAsync(albumUrl);
                if (albumResponse.IsSuccessStatusCode)
                {
                    var albumJson = await albumResponse.Content.ReadAsStringAsync();
                    var albumData = JsonDocument.Parse(albumJson).RootElement;
                    
                    // Genre
                    if (albumData.TryGetProperty("genres", out var genres) && 
                        genres.TryGetProperty("data", out var genresData) &&
                        genresData.GetArrayLength() > 0 &&
                        genresData[0].TryGetProperty("name", out var genreName))
                    {
                        song.Genre = genreName.GetString();
                    }
                    
                    // Total track count
                    if (albumData.TryGetProperty("nb_tracks", out var nbTracks))
                    {
                        song.TotalTracks = nbTracks.GetInt32();
                    }
                    
                    // Label
                    if (albumData.TryGetProperty("label", out var label))
                    {
                        song.Label = label.GetString();
                    }
                    
                    // Cover art XL if not already set
                    if (string.IsNullOrEmpty(song.CoverArtUrlLarge))
                    {
                        if (albumData.TryGetProperty("cover_xl", out var coverXl))
                        {
                            song.CoverArtUrlLarge = coverXl.GetString();
                        }
                        else if (albumData.TryGetProperty("cover_big", out var coverBig))
                        {
                            song.CoverArtUrlLarge = coverBig.GetString();
                        }
                    }
                }
            }
            catch
            {
                // If we can't get the album, continue with track info only
            }
        }
        
        return song;
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "deezer") return null;
        
        var url = $"{BaseUrl}/album/{externalId}";
        var response = await _httpClient.GetAsync(url);
        
        if (!response.IsSuccessStatusCode) return null;
        
        var json = await response.Content.ReadAsStringAsync();
        var albumElement = JsonDocument.Parse(json).RootElement;
        
        if (albumElement.TryGetProperty("error", out _)) return null;
        
        var album = ParseDeezerAlbum(albumElement);

        // Deezer /album/{id} embeds only the first 25 tracks in tracks.data.
        // Use the tracklist endpoint and follow pagination to load all tracks.
        if (albumElement.TryGetProperty("tracklist", out var tracklistEl))
        {
            var tracklistUrl = tracklistEl.GetString();
            if (!string.IsNullOrWhiteSpace(tracklistUrl))
            {
                var nextPageUrl = $"{tracklistUrl}?limit=1000";
                int trackIndex = 1;

                while (!string.IsNullOrWhiteSpace(nextPageUrl))
                {
                    var tracklistResponse = await _httpClient.GetAsync(nextPageUrl);
                    if (!tracklistResponse.IsSuccessStatusCode) break;

                    var tracklistJson = await tracklistResponse.Content.ReadAsStringAsync();
                    var tracklistElement = JsonDocument.Parse(tracklistJson).RootElement;

                    if (!tracklistElement.TryGetProperty("data", out var pageTracks)) break;

                    foreach (var track in pageTracks.EnumerateArray())
                    {
                        // Pass the album artist to ensure proper folder organization
                        var song = ParseDeezerTrack(track, trackIndex, album.Artist);

                        // Ensure album metadata is set (tracks in album response may not have full album object)
                        song.Album = album.Title;
                        song.AlbumId = album.Id;
                        song.AlbumArtist = album.Artist;
                        song.Year ??= album.Year;
                        song.Genre ??= album.Genre;
                        song.ReleaseType ??= album.ReleaseType;
                        song.TotalTracks ??= album.SongCount;
                        song.CoverArtUrl ??= album.CoverArtUrl;
                        song.CoverArtUrlLarge ??= album.CoverArtUrlLarge;

                        if (ShouldIncludeSong(song))
                        {
                            album.Songs.Add(song);
                        }
                        trackIndex++;
                    }

                    nextPageUrl = tracklistElement.TryGetProperty("next", out var nextEl)
                        ? nextEl.GetString()
                        : null;
                }

                return album;
            }
        }

        // Fallback for unexpected responses without a tracklist URL.
        // Get album songs
        if (albumElement.TryGetProperty("tracks", out var tracks) &&
            tracks.TryGetProperty("data", out var tracksData))
        {
            int trackIndex = 1;
            foreach (var track in tracksData.EnumerateArray())
            {
                // Pass the album artist to ensure proper folder organization
                var song = ParseDeezerTrack(track, trackIndex, album.Artist);

                // Ensure album metadata is set (tracks in album response may not have full album object)
                song.Album = album.Title;
                song.AlbumId = album.Id;
                song.AlbumArtist = album.Artist;
                song.Year ??= album.Year;
                song.Genre ??= album.Genre;
                song.ReleaseType ??= album.ReleaseType;
                song.TotalTracks ??= album.SongCount;
                song.CoverArtUrl ??= album.CoverArtUrl;
                song.CoverArtUrlLarge ??= album.CoverArtUrlLarge;

                if (ShouldIncludeSong(song))
                {
                    album.Songs.Add(song);
                }
                trackIndex++;
            }
        }

        return album;
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "deezer") return null;
        
        var url = $"{BaseUrl}/artist/{externalId}";
        var response = await _httpClient.GetAsync(url);
        
        if (!response.IsSuccessStatusCode) return null;
        
        var json = await response.Content.ReadAsStringAsync();
        var artist = JsonDocument.Parse(json).RootElement;
        
        if (artist.TryGetProperty("error", out _)) return null;
        
        return ParseDeezerArtist(artist);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "deezer") return new List<Album>();

        // Prefer the private gateway so the discography isn't truncated by the
        // server's IP geo-filtering (issue #232). Fall back to the public API.
        var gatewayAlbums = await GetArtistAlbumsViaGatewayAsync(externalId);
        if (gatewayAlbums != null) return gatewayAlbums;

        var url = $"{BaseUrl}/artist/{externalId}/albums";
        var response = await _httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode) return new List<Album>();
        
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(json);
        
        var albums = new List<Album>();
        if (result.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var album in data.EnumerateArray())
            {
                albums.Add(ParseDeezerAlbum(album));
            }
        }
        
        return albums;
    }

    private Song ParseDeezerTrack(JsonElement track, int? fallbackTrackNumber = null, string? albumArtist = null)
    {
        var externalId = track.GetProperty("id").GetInt64().ToString();
        
        // Try to get track_position from API, fallback to provided index
        int? trackNumber = track.TryGetProperty("track_position", out var trackPos) 
            ? trackPos.GetInt32() 
            : fallbackTrackNumber;
        
        // Explicit content lyrics value
        int? explicitContentLyrics = track.TryGetProperty("explicit_content_lyrics", out var ecl) 
            ? ecl.GetInt32() 
            : null;
        
        var mainArtist = track.TryGetProperty("artist", out var artist) 
            ? artist.GetProperty("name").GetString() ?? "" 
            : "";
        
        return new Song
        {
            Title = track.GetProperty("title").GetString() ?? "",
            Artist = mainArtist,
            Artists = track.TryGetProperty("artist", out var artistForList)
                ? new List<Artist> { ParseDeezerArtist(artistForList) }
                : new List<Artist>(),
            ArtistId = track.TryGetProperty("artist", out var artistForId)
                ? $"ext-deezer-artist-{artistForId.GetProperty("id").GetInt64()}"
                : null,
            Album = track.TryGetProperty("album", out var album)
                ? album.GetProperty("title").GetString() ?? ""
                : "",
            AlbumId = track.TryGetProperty("album", out var albumForId)
                ? $"ext-deezer-album-{albumForId.GetProperty("id").GetInt64()}"
                : null,
            Duration = track.TryGetProperty("duration", out var duration)
                ? duration.GetInt32()
                : null,
            Track = trackNumber,
            CoverArtUrl = track.TryGetProperty("album", out var albumForCover) &&
                          albumForCover.TryGetProperty("cover_medium", out var cover)
                ? cover.GetString()
                : null,
            CoverArtUrlLarge = track.TryGetProperty("album", out var albumForCoverXL) && 
                            albumForCoverXL.TryGetProperty("cover_xl", out var coverxl)
                ? coverxl.GetString()
                : (track.TryGetProperty("album", out var albumForCoverBig) &&
                   albumForCoverBig.TryGetProperty("cover_big", out var coverBig)
                    ? coverBig.GetString()
                    : null),
            AlbumArtist = albumArtist,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId,
            ExplicitContentLyrics = explicitContentLyrics
        };
    }

    /// <summary>
    /// Parses a Deezer track with all available metadata
    /// Used for GetSongAsync which returns complete data
    /// </summary>
    private Song ParseDeezerTrackFull(JsonElement track)
    {
        var externalId = track.GetProperty("id").GetInt64().ToString();
        
        // Track position et disc number
        int? trackNumber = track.TryGetProperty("track_position", out var trackPos) 
            ? trackPos.GetInt32() 
            : null;
        int? discNumber = track.TryGetProperty("disk_number", out var diskNum) 
            ? diskNum.GetInt32() 
            : null;
        
        // BPM
        int? bpm = track.TryGetProperty("bpm", out var bpmVal) && bpmVal.ValueKind == JsonValueKind.Number
            ? (int)bpmVal.GetDouble() 
            : null;
        
        // ISRC
        string? isrc = track.TryGetProperty("isrc", out var isrcVal) 
            ? isrcVal.GetString() 
            : null;
        
        // Release date from album
        string? releaseDate = null;
        int? year = null;
        if (track.TryGetProperty("release_date", out var relDate))
        {
            releaseDate = relDate.GetString();
            if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4)
            {
                if (int.TryParse(releaseDate.Substring(0, 4), out var y))
                    year = y;
            }
        }
        else if (track.TryGetProperty("album", out var albumForDate) && 
                 albumForDate.TryGetProperty("release_date", out var albumRelDate))
        {
            releaseDate = albumRelDate.GetString();
            if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4)
            {
                if (int.TryParse(releaseDate.Substring(0, 4), out var y))
                    year = y;
            }
        }
        
        // Contributors. Deezer lists all performing artists here (with their ids),
        // so we use them both for the Contributors names and the typed Artists list.
        var contributors = new List<string>();
        var contributorArtists = new List<Artist>();
        if (track.TryGetProperty("contributors", out var contribs))
        {
            foreach (var contrib in contribs.EnumerateArray())
            {
                if (contrib.TryGetProperty("name", out var contribName))
                {
                    var name = contribName.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        contributors.Add(name);
                        contributorArtists.Add(ParseDeezerArtist(contrib));
                    }
                }
            }
        }
        
        // Album artist (first artist from album, or main track artist)
        string? albumArtist = null;
        if (track.TryGetProperty("album", out var albumForArtist) && 
            albumForArtist.TryGetProperty("artist", out var albumArtistEl))
        {
            albumArtist = albumArtistEl.TryGetProperty("name", out var aName) 
                ? aName.GetString() 
                : null;
        }
        
        // Cover art URLs (different sizes)
        string? coverMedium = null;
        string? coverLarge = null;
        if (track.TryGetProperty("album", out var albumForCover))
        {
            coverMedium = albumForCover.TryGetProperty("cover_medium", out var cm) 
                ? cm.GetString() 
                : null;
            coverLarge = albumForCover.TryGetProperty("cover_xl", out var cxl) 
                ? cxl.GetString() 
                : (albumForCover.TryGetProperty("cover_big", out var cb) ? cb.GetString() : null);
        }
        
        // Explicit content lyrics value
        int? explicitContentLyrics = track.TryGetProperty("explicit_content_lyrics", out var ecl) 
            ? ecl.GetInt32() 
            : null;
        
        var mainArtist = track.TryGetProperty("artist", out var artist) 
            ? artist.GetProperty("name").GetString() ?? "" 
            : "";
        
        return new Song
        {
            Title = track.GetProperty("title").GetString() ?? "",
            Artist = mainArtist,
            Artists = contributorArtists.Count > 0
                ? contributorArtists
                : (track.TryGetProperty("artist", out var mainArtistForList)
                    ? new List<Artist> { ParseDeezerArtist(mainArtistForList) }
                    : new List<Artist>()),
            ArtistId = track.TryGetProperty("artist", out var artistForId)
                ? $"ext-deezer-artist-{artistForId.GetProperty("id").GetInt64()}" 
                : null,
            Album = track.TryGetProperty("album", out var album) 
                ? album.GetProperty("title").GetString() ?? "" 
                : "",
            AlbumId = track.TryGetProperty("album", out var albumForId) 
                ? $"ext-deezer-album-{albumForId.GetProperty("id").GetInt64()}" 
                : null,
            Duration = track.TryGetProperty("duration", out var duration) 
                ? duration.GetInt32() 
                : null,
            Track = trackNumber,
            DiscNumber = discNumber,
            Year = year,
            Bpm = bpm,
            Isrc = isrc,
            ReleaseDate = releaseDate,
            AlbumArtist = albumArtist,
            Contributors = contributors,
            CoverArtUrl = coverMedium,
            CoverArtUrlLarge = coverLarge,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId,
            ExplicitContentLyrics = explicitContentLyrics
        };
    }

    private Album ParseDeezerAlbum(JsonElement album)
    {
        var externalId = album.GetProperty("id").GetInt64().ToString();
        
        return new Album
        {
            Id = $"ext-deezer-album-{externalId}",
            Title = album.GetProperty("title").GetString() ?? "",
            Artist = album.TryGetProperty("artist", out var artist) 
                ? artist.GetProperty("name").GetString() ?? "" 
                : "",
            ArtistId = album.TryGetProperty("artist", out var artistForId) 
                ? $"ext-deezer-artist-{artistForId.GetProperty("id").GetInt64()}" 
                : null,
            Year = album.TryGetProperty("release_date", out var releaseDate) 
                ? int.TryParse(releaseDate.GetString()?.Split('-')[0], out var year) ? year : null
                : null,
            SongCount = album.TryGetProperty("nb_tracks", out var nbTracks) 
                ? nbTracks.GetInt32() 
                : null,
            CoverArtUrl = album.TryGetProperty("cover_medium", out var cover)
                ? cover.GetString()
                : null,
            CoverArtUrlLarge = album.TryGetProperty("cover_xl", out var coverXl)
                ? coverXl.GetString()
                : (album.TryGetProperty("cover_big", out var coverBig)
                    ? coverBig.GetString()
                    : null),
            Genre = album.TryGetProperty("genres", out var genres) && 
                    genres.TryGetProperty("data", out var genresData) &&
                    genresData.GetArrayLength() > 0
                ? genresData[0].GetProperty("name").GetString()
                : null,
            ReleaseType = album.TryGetProperty("record_type", out var recordType) 
                ? recordType.GetString() 
                : null,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId
        };
    }

    private Artist ParseDeezerArtist(JsonElement artist)
    {
        var externalId = artist.GetProperty("id").GetInt64().ToString();
        
        return new Artist
        {
            Id = $"ext-deezer-artist-{externalId}",
            Name = artist.GetProperty("name").GetString() ?? "",
            ImageUrl = artist.TryGetProperty("picture_big", out var pictureBig) 
                ? pictureBig.GetString() 
                : (artist.TryGetProperty("picture_medium", out var picture) 
                    ? picture.GetString()
                    : null),
            AlbumCount = artist.TryGetProperty("nb_album", out var nbAlbum) 
                ? nbAlbum.GetInt32() 
                : null,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId
        };
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        try
        {
            var url = $"{BaseUrl}/search/playlist?q={Uri.EscapeDataString(query)}&limit={limit}";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode) return new List<ExternalPlaylist>();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            var playlists = new List<ExternalPlaylist>();
            if (result.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var playlist in data.EnumerateArray())
                {
                    playlists.Add(ParseDeezerPlaylist(playlist));
                }
            }
            
            return playlists;
        }
        catch
        {
            return new List<ExternalPlaylist>();
        }
    }
    
    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "deezer") return null;
        
        try
        {
            var url = $"{BaseUrl}/playlist/{externalId}";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode) return null;
            
            var json = await response.Content.ReadAsStringAsync();
            var playlistElement = JsonDocument.Parse(json).RootElement;
            
            if (playlistElement.TryGetProperty("error", out _)) return null;
            
            return ParseDeezerPlaylist(playlistElement);
        }
        catch
        {
            return null;
        }
    }
    
    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "deezer") return new List<Song>();
        
        try
        {
            var url = $"{BaseUrl}/playlist/{externalId}";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode) return new List<Song>();
            
            var json = await response.Content.ReadAsStringAsync();
            var playlistElement = JsonDocument.Parse(json).RootElement;
            
            if (playlistElement.TryGetProperty("error", out _)) return new List<Song>();
            
            var songs = new List<Song>();
            
            // Get playlist name for album field
            var playlistName = playlistElement.TryGetProperty("title", out var titleEl)
                ? titleEl.GetString() ?? "Unknown Playlist"
                : "Unknown Playlist";

            // Deezer playlist/{id} embeds at most 400 tracks in tracks.data.
            // Use the dedicated tracklist endpoint and follow pagination to load all tracks.
            if (playlistElement.TryGetProperty("tracklist", out var tracklistEl))
            {
                var tracklistUrl = tracklistEl.GetString();
                if (!string.IsNullOrWhiteSpace(tracklistUrl))
                {
                    var nextPageUrl = $"{tracklistUrl}?limit=1000";

                    while (!string.IsNullOrWhiteSpace(nextPageUrl))
                    {
                        var tracklistResponse = await _httpClient.GetAsync(nextPageUrl);
                        if (!tracklistResponse.IsSuccessStatusCode)
                        {
                            break;
                        }

                        var tracklistJson = await tracklistResponse.Content.ReadAsStringAsync();
                        var tracklistElement = JsonDocument.Parse(tracklistJson).RootElement;

                        if (!tracklistElement.TryGetProperty("data", out var pageTracks))
                        {
                            break;
                        }
                        
                        foreach (var track in pageTracks.EnumerateArray())
                        {
                            // For playlists, use the track's own artist (not a single album artist)
                            var song = ParseDeezerTrack(track);

                            // Override album name to be the playlist name
                            song.Album = playlistName;

                            if (ShouldIncludeSong(song))
                            {
                                song.Track = songs.Count + 1;
                                songs.Add(song);
                            }
                        }

                        nextPageUrl = tracklistElement.TryGetProperty("next", out var nextEl)
                            ? nextEl.GetString()
                            : null;
                    }

                    return songs;
                }
            }

            // Fallback for unexpected Deezer responses without tracklist URL.
            if (playlistElement.TryGetProperty("tracks", out var tracks) &&
                tracks.TryGetProperty("data", out var tracksData))
            {
                foreach (var track in tracksData.EnumerateArray())
                {
                    var song = ParseDeezerTrack(track);
                    song.Album = playlistName;

                    if (ShouldIncludeSong(song))
                    {
                        song.Track = songs.Count + 1;
                        songs.Add(song);
                    }
                }
            }
            
            return songs;
        }
        catch
        {
            return new List<Song>();
        }
    }

    private ExternalPlaylist ParseDeezerPlaylist(JsonElement playlist)
    {
        var externalId = playlist.GetProperty("id").GetInt64().ToString();
        
        // Get curator/creator name
        string? curatorName = null;
        if (playlist.TryGetProperty("user", out var user) &&
            user.TryGetProperty("name", out var userName))
        {
            curatorName = userName.GetString();
        }
        else if (playlist.TryGetProperty("creator", out var creator) &&
                 creator.TryGetProperty("name", out var creatorName))
        {
            curatorName = creatorName.GetString();
        }
        
        // Get creation date
        DateTime? createdDate = null;
        if (playlist.TryGetProperty("creation_date", out var creationDateEl))
        {
            var dateStr = creationDateEl.GetString();
            if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
            {
                createdDate = date;
            }
        }
        
        return new ExternalPlaylist
        {
            Id = Common.PlaylistIdHelper.CreatePlaylistId("deezer", externalId),
            Name = playlist.GetProperty("title").GetString() ?? "",
            Description = playlist.TryGetProperty("description", out var desc) 
                ? desc.GetString() 
                : null,
            CuratorName = curatorName,
            Provider = "deezer",
            ExternalId = externalId,
            TrackCount = playlist.TryGetProperty("nb_tracks", out var nbTracks) 
                ? nbTracks.GetInt32() 
                : 0,
            Duration = playlist.TryGetProperty("duration", out var duration) 
                ? duration.GetInt32() 
                : 0,
            CoverUrl = playlist.TryGetProperty("picture_big", out var pictureBig) 
                    ? pictureBig.GetString() 
                    : (playlist.TryGetProperty("picture_medium", out var picture) 
                    ? picture.GetString()
                    : null),
            CreatedDate = createdDate
        };
    }

    /// <summary>
    /// Determines whether a song should be included based on the explicit content filter setting
    /// </summary>
    /// <param name="song">The song to check</param>
    /// <returns>True if the song should be included, false otherwise</returns>
    private bool ShouldIncludeSong(Song song)
    {
        // If no explicit content info, include the song
        if (song.ExplicitContentLyrics == null)
            return true;
        
        return _settings.ExplicitFilter switch
        {
            // All: No filtering, include everything
            ExplicitFilter.All => true,
            
            // ExplicitOnly: Exclude clean/edited versions (value 3)
            // Include: 0 (naturally clean), 1 (explicit), 2 (not applicable), 6/7 (unknown)
            ExplicitFilter.ExplicitOnly => song.ExplicitContentLyrics != 3,
            
            // CleanOnly: Only show clean content
            // Include: 0 (naturally clean), 3 (clean/edited version)
            // Exclude: 1 (explicit)
            ExplicitFilter.CleanOnly => song.ExplicitContentLyrics != 1,
            
            _ => true
        };
    }
}
