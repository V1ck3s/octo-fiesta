using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Models.SquidWTF;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Metadata service implementation using SquidWTF API
/// Supports Qobuz, Tidal, and Amazon Music backends
/// </summary>
public class SquidWTFMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly SquidWTFSettings _settings;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly SquidWTFInstanceManager _instanceManager;
    private readonly SquidWTFCaptchaSolver _captchaSolver;
    private readonly ILogger<SquidWTFMetadataService> _logger;

    // Cover URL cache: ASIN → cover URL, populated from search results so getCoverArt
    // can serve covers without making an extra /api/track call.
    // Capped at 2000 entries; cleared when full to avoid unbounded growth on long-running instances.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _coverCache = new();
    private const int CoverCacheMaxEntries = 2000;

    public string? GetCachedCoverUrl(string asin) =>
        _coverCache.TryGetValue(asin, out var url) ? url : null;

    private void CacheCoverUrl(string asin, string url)
    {
        if (_coverCache.Count >= CoverCacheMaxEntries)
            _coverCache.Clear();
        _coverCache[asin] = url;
    }

    private string QobuzBaseUrl => _settings.EffectiveQobuzBaseUrl;
    private string AmazonBaseUrl => _settings.EffectiveAmazonBaseUrl;

    // Required headers
    private const string QobuzCountryHeader = "Token-Country";
    private const string QobuzCountryValue = "US";
    private const string TidalClientHeader = "x-client";
    private const string TidalClientValue = "BiniLossless/v3.4";
    private const string AmazonCaptchaTokenHeader = "X-Captcha-Token";

    private bool IsQobuzSource => _settings.Source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase);
    private bool IsAmazonSource => _settings.Source.Equals("AmazonMusic", StringComparison.OrdinalIgnoreCase);

    public SquidWTFMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<SquidWTFSettings> settings,
        IOptions<SubsonicSettings> subsonicSettings,
        SquidWTFInstanceManager instanceManager,
        SquidWTFCaptchaSolver captchaSolver,
        ILogger<SquidWTFMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _subsonicSettings = subsonicSettings.Value;
        _instanceManager = instanceManager;
        _captchaSolver = captchaSolver;
        _logger = logger;
    }

    #region IMusicMetadataService Implementation

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        try
        {
            if (IsQobuzSource)
                return await SearchSongsQobuzAsync(query, limit);
            if (IsAmazonSource)
                return await SearchSongsAmazonAsync(query, limit);
            return await SearchSongsTidalAsync(query, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search songs for query: {Query}", query);
            return new List<Song>();
        }
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        try
        {
            if (IsQobuzSource)
                return await SearchAlbumsQobuzAsync(query, limit);
            if (IsAmazonSource)
                return await SearchAlbumsAmazonAsync(query, limit);
            return await SearchAlbumsTidalAsync(query, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search albums for query: {Query}", query);
            return new List<Album>();
        }
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        try
        {
            if (IsQobuzSource)
                return await SearchArtistsQobuzAsync(query, limit);
            if (IsAmazonSource)
                return new List<Artist>(); // Amazon Music API doesn't expose artist search
            return await SearchArtistsTidalAsync(query, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search artists for query: {Query}", query);
            return new List<Artist>();
        }
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var songsTask = SearchSongsAsync(query, songLimit);
        var albumsTask = SearchAlbumsAsync(query, albumLimit);
        var artistsTask = SearchArtistsAsync(query, artistLimit);
        
        await Task.WhenAll(songsTask, albumsTask, artistsTask);
        
        var songs = await songsTask;
        var albums = await albumsTask;
        var artists = await artistsTask;
        
        // Cross-reference artists with albums to populate AlbumCount
        // This avoids extra API calls since we already have album results
        if (artists.Count > 0 && albums.Count > 0)
        {
            foreach (var artist in artists)
            {
                if (artist.AlbumCount == null || artist.AlbumCount == 0)
                {
                    var matchingAlbums = albums.Count(a => a.ArtistId == artist.Id);
                    if (matchingAlbums > 0)
                    {
                        artist.AlbumCount = matchingAlbums;
                    }
                }
            }
        }
        
        return new SearchResult
        {
            Songs = songs,
            Albums = albums,
            Artists = artists
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return null;

        try
        {
            if (IsQobuzSource)
                return await GetSongQobuzAsync(externalId);
            if (IsAmazonSource)
                return await GetSongAmazonAsync(externalId);
            return await GetSongTidalAsync(externalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get song: {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return null;

        try
        {
            if (IsQobuzSource)
                return await GetAlbumQobuzAsync(externalId);
            if (IsAmazonSource)
                return await GetAlbumAmazonAsync(externalId);
            return await GetAlbumTidalAsync(externalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get album: {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return null;

        try
        {
            if (IsQobuzSource)
                return await GetArtistQobuzAsync(externalId);
            if (IsAmazonSource)
                return null; // Amazon Music API doesn't expose individual artist lookup
            return await GetArtistTidalAsync(externalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artist: {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return new List<Album>();

        try
        {
            if (IsQobuzSource)
                return await GetArtistAlbumsQobuzAsync(externalId);
            if (IsAmazonSource)
                return new List<Album>(); // Amazon Music API doesn't expose artist album lists
            return await GetArtistAlbumsTidalAsync(externalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artist albums: {ExternalId}", externalId);
            return new List<Album>();
        }
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        try
        {
            // Only Tidal supports playlist search via SquidWTF
            if (!IsQobuzSource && !IsAmazonSource)
                return await SearchPlaylistsTidalAsync(query, limit);

            return new List<ExternalPlaylist>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search playlists for query: {Query}", query);
            return new List<ExternalPlaylist>();
        }
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return null;

        try
        {
            if (!IsQobuzSource && !IsAmazonSource)
                return await GetPlaylistTidalAsync(externalId);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlist: {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return new List<Song>();

        try
        {
            if (!IsQobuzSource && !IsAmazonSource)
                return await GetPlaylistTracksTidalAsync(externalId);

            return new List<Song>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlist tracks: {ExternalId}", externalId);
            return new List<Song>();
        }
    }

    #endregion

    #region Qobuz Backend Methods

    private async Task<List<Song>> SearchSongsQobuzAsync(string query, int limit)
    {
        var url = $"{QobuzBaseUrl}/api/get-music?q={Uri.EscapeDataString(query)}&offset=0";
        var response = await SendQobuzRequestAsync(url);
        
        if (response == null) return new List<Song>();
        
        var searchResponse = JsonSerializer.Deserialize<QobuzSearchResponse>(response);
        if (searchResponse?.Data?.Tracks?.Items == null) return new List<Song>();
        
        var songs = new List<Song>();
        foreach (var track in searchResponse.Data.Tracks.Items.Take(limit))
        {
            var song = MapQobuzTrackToSong(track);
            if (ShouldIncludeSong(song))
            {
                songs.Add(song);
            }
        }
        
        return songs;
    }

    private async Task<List<Album>> SearchAlbumsQobuzAsync(string query, int limit)
    {
        var url = $"{QobuzBaseUrl}/api/get-music?q={Uri.EscapeDataString(query)}&offset=0";
        var response = await SendQobuzRequestAsync(url);
        
        if (response == null) return new List<Album>();
        
        var searchResponse = JsonSerializer.Deserialize<QobuzSearchResponse>(response);
        if (searchResponse?.Data?.Albums?.Items == null) return new List<Album>();
        
        return searchResponse.Data.Albums.Items
            .Take(limit)
            .Select(MapQobuzAlbumToAlbum)
            .ToList();
    }

    private async Task<List<Artist>> SearchArtistsQobuzAsync(string query, int limit)
    {
        var url = $"{QobuzBaseUrl}/api/get-music?q={Uri.EscapeDataString(query)}&offset=0";
        var response = await SendQobuzRequestAsync(url);
        
        if (response == null) return new List<Artist>();
        
        var searchResponse = JsonSerializer.Deserialize<QobuzSearchResponse>(response);
        if (searchResponse?.Data?.Artists?.Items == null) return new List<Artist>();
        
        return searchResponse.Data.Artists.Items
            .Take(limit)
            .Select(MapQobuzArtistToArtist)
            .ToList();
    }

    private async Task<Song?> GetSongQobuzAsync(string trackId)
    {
        // Qobuz doesn't have a direct track endpoint, get from album
        // For now, return a basic song object - full metadata will come from album
        var url = $"{QobuzBaseUrl}/api/get-music?q={trackId}&offset=0";
        var response = await SendQobuzRequestAsync(url);
        
        if (response == null) return null;
        
        var searchResponse = JsonSerializer.Deserialize<QobuzSearchResponse>(response);
        var track = searchResponse?.Data?.Tracks?.Items?.FirstOrDefault(t => t.Id.ToString() == trackId);
        
        if (track == null) return null;
        
        return MapQobuzTrackToSong(track);
    }

    private async Task<Album?> GetAlbumQobuzAsync(string albumId)
    {
        var url = $"{QobuzBaseUrl}/api/get-album?album_id={albumId}";
        var response = await SendQobuzRequestAsync(url);
        
        if (response == null) return null;
        
        var albumResponse = JsonSerializer.Deserialize<QobuzAlbumResponse>(response);
        if (albumResponse?.Data == null) return null;
        
        var album = MapQobuzAlbumToAlbum(albumResponse.Data);
        
        // Add tracks
        if (albumResponse.Data.Tracks?.Items != null)
        {
            foreach (var track in albumResponse.Data.Tracks.Items)
            {
                var song = MapQobuzTrackToSong(track);
                song.Album = album.Title;
                song.AlbumId = album.Id;
                song.AlbumArtist = album.Artist;
                song.Year ??= album.Year;
                song.Genre ??= album.Genre;
                song.TotalTracks ??= album.SongCount;
                song.ReleaseType ??= album.ReleaseType;
                
                // Use album cover for tracks if track doesn't have one (common for tracks from /api/get-album)
                if (string.IsNullOrEmpty(song.CoverArtUrl))
                {
                    song.CoverArtUrl = album.CoverArtUrl;
                }
                if (string.IsNullOrEmpty(song.CoverArtUrlLarge))
                {
                    song.CoverArtUrlLarge = album.CoverArtUrlLarge;
                }
                
                if (ShouldIncludeSong(song))
                {
                    album.Songs.Add(song);
                }
            }
        }
        
        return album;
    }

    private async Task<Artist?> GetArtistQobuzAsync(string artistId)
    {
        var url = $"{QobuzBaseUrl}/api/get-artist?artist_id={artistId}";
        var response = await SendQobuzRequestAsync(url);
        
        if (response == null) return null;
        
        var artistResponse = JsonSerializer.Deserialize<QobuzArtistResponse>(response);
        if (artistResponse?.Data?.Artist == null) return null;
        
        return MapQobuzArtistToArtist(artistResponse.Data.Artist);
    }

    private async Task<List<Album>> GetArtistAlbumsQobuzAsync(string artistId)
    {
        var artist = await GetArtistQobuzAsync(artistId);
        if (artist == null) return new List<Album>();
        
        // Search for albums by artist name (Qobuz get-artist doesn't return albums list)
        var url = $"{QobuzBaseUrl}/api/get-music?q={Uri.EscapeDataString(artist.Name)}&offset=0";
        var response = await SendQobuzRequestAsync(url);
        
        if (response == null) return new List<Album>();
        
        var searchResponse = JsonSerializer.Deserialize<QobuzSearchResponse>(response);
        if (searchResponse?.Data?.Albums?.Items == null) return new List<Album>();
        
        // Filter albums that have this artist as main artist
        return searchResponse.Data.Albums.Items
            .Where(a => a.Artist?.Id.ToString() == artistId)
            .Select(MapQobuzAlbumToAlbum)
            .ToList();
    }

    private async Task<string?> SendQobuzRequestAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(QobuzCountryHeader, QobuzCountryValue);
            
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Qobuz API returned {StatusCode} for {Url}", response.StatusCode, url);
                return null;
            }
            
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Qobuz request to {Url}", url);
            return null;
        }
    }

    #endregion

    #region Amazon Music Backend Methods

    private async Task<List<Song>> SearchSongsAmazonAsync(string query, int limit)
    {
        var response = await SendAmazonPostAsync("/api/search",
            new { query, country = _settings.Country, content_type = "TRACK", limit = Math.Max(limit, 25) });

        if (response == null) return new List<Song>();

        var searchResponse = JsonSerializer.Deserialize<AmazonMusicSearchResponse>(response);
        if (searchResponse?.TrackList == null) return new List<Song>();

        return searchResponse.TrackList
            .Where(t => !string.IsNullOrEmpty(t.Asin))
            .Take(limit)
            .Select(MapAmazonSearchTrackToSong)
            .Where(ShouldIncludeSong)
            .ToList();
    }

    private async Task<List<Album>> SearchAlbumsAmazonAsync(string query, int limit)
    {
        var response = await SendAmazonPostAsync("/api/search",
            new { query, country = _settings.Country, content_type = "ALBUM", limit = Math.Max(limit, 25) });

        if (response == null) return new List<Album>();

        var searchResponse = JsonSerializer.Deserialize<AmazonMusicSearchResponse>(response);
        if (searchResponse?.AlbumList == null) return new List<Album>();

        return searchResponse.AlbumList
            .Where(a => !string.IsNullOrEmpty(a.Asin))
            .Take(limit)
            .Select(MapAmazonSearchAlbumToAlbum)
            .ToList();
    }

    private async Task<Song?> GetSongAmazonAsync(string trackAsin)
    {
        var response = await SendAmazonPostAsync("/api/track",
            new { asin = trackAsin, tier = "best", country = _settings.Country });

        if (response == null) return null;

        var trackResponse = JsonSerializer.Deserialize<AmazonMusicTrackResponse>(response);
        if (trackResponse?.Metadata == null) return null;

        return MapAmazonTrackMetadataToSong(trackAsin, trackResponse.Metadata);
    }

    private async Task<Album?> GetAlbumAmazonAsync(string albumAsin)
    {
        // Use /api/queue with the constructed Amazon Music album URL to get track listing
        var albumUrl = BuildAmazonAlbumUrl(albumAsin, _settings.Country);
        var response = await SendAmazonPostAsync("/api/queue",
            new { url = albumUrl, country = _settings.Country });

        if (response == null) return null;

        var queueResponse = JsonSerializer.Deserialize<AmazonMusicQueueResponse>(response);
        if (queueResponse?.Queue == null || queueResponse.Queue.Count == 0) return null;

        var firstItem = queueResponse.Queue[0];
        var album = new Album
        {
            Id = $"ext-squidwtf-album-{albumAsin}",
            Title = firstItem.Album ?? "",
            Artist = firstItem.AlbumArtist ?? "",
            ArtistId = null,
            Year = ParseAmazonYear(firstItem.Year, firstItem.Date),
            SongCount = queueResponse.Queue.Count,
            CoverArtUrl = ResolveAmazonCoverUrl(firstItem.Cover ?? firstItem.Thumbnail),
            CoverArtUrlLarge = ResolveAmazonCoverUrl(firstItem.Cover ?? firstItem.Thumbnail),
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = albumAsin
        };

        foreach (var item in queueResponse.Queue)
        {
            if (string.IsNullOrEmpty(item.Asin)) continue;

            var song = MapAmazonQueueItemToSong(item);
            song.Album = album.Title;
            song.AlbumId = album.Id;
            song.AlbumArtist = album.Artist;
            song.Year ??= album.Year;
            song.TotalTracks ??= album.SongCount;

            if (string.IsNullOrEmpty(song.CoverArtUrl))
                song.CoverArtUrl = album.CoverArtUrl;
            if (string.IsNullOrEmpty(song.CoverArtUrlLarge))
                song.CoverArtUrlLarge = album.CoverArtUrlLarge;

            if (ShouldIncludeSong(song))
                album.Songs.Add(song);
        }

        return album;
    }

    private async Task<string?> SendAmazonPostAsync(string path, object body)
    {
        try
        {
            var json = JsonSerializer.Serialize(body);
            using var response = await _captchaSolver.SendAmazonPostAsync(AmazonBaseUrl, path, json);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Amazon Music API returned {StatusCode} for {Path}", response.StatusCode, path);
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Amazon Music request to {Path}", path);
            return null;
        }
    }

    private string BuildAmazonAlbumUrl(string albumAsin, string country)
    {
        var domain = country switch
        {
            "DE" => "music.amazon.de",
            "AU" => "music.amazon.com.au",
            _ => "music.amazon.com"
        };
        return $"https://{domain}/albums/{albumAsin}";
    }

    private string? ResolveAmazonCoverUrl(string? cover)
    {
        if (string.IsNullOrEmpty(cover)) return null;
        if (cover.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return cover;
        if (cover.StartsWith("/")) return $"{AmazonBaseUrl}{cover}";
        return $"{AmazonBaseUrl}/api/image?url={Uri.EscapeDataString(cover)}";
    }

    private static int? ParseAmazonYear(string? year, string? date)
    {
        if (!string.IsNullOrEmpty(year) && int.TryParse(year, out var y)) return y;
        if (!string.IsNullOrEmpty(date) && date.Length >= 4 && int.TryParse(date[..4], out var dy)) return dy;
        return null;
    }

    private static int? ParseAmazonJsonElementAsInt(JsonElement? element)
    {
        if (element == null) return null;
        var e = element.Value;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i)) return i;
        if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var s)) return s;
        return null;
    }

    #endregion

    #region Mapping Methods - Amazon Music

    private Song MapAmazonSearchTrackToSong(AmazonMusicSearchTrack track)
    {
        var externalId = track.Asin!;
        var artistName = track.PrimaryArtistName ?? track.ArtistName ?? track.AlbumArtistName ?? "";
        var albumTitle = track.Album?.Title ?? "";
        var coverUrl = ResolveAmazonCoverUrl(track.Album?.Image ?? track.Image ?? track.Cover);
        if (coverUrl != null) CacheCoverUrl(externalId, coverUrl);

        // Use the song's own external ID as AlbumId so clients that use albumId for cover
        // art lookup (instead of the coverArt attribute) still call getCoverArt correctly.
        // Amazon search results don't include an album ASIN, so there's no real album to link.
        var songId = $"ext-squidwtf-song-{externalId}";
        return new Song
        {
            Title = track.Title ?? "",
            Artist = artistName,
            Artists = !string.IsNullOrEmpty(artistName)
                ? new List<Artist> { new Artist { Id = "", Name = artistName, IsLocal = false, ExternalProvider = "squidwtf" } }
                : new List<Artist>(),
            Album = albumTitle,
            AlbumId = songId,
            CoverArtUrl = coverUrl,
            CoverArtUrlLarge = coverUrl,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

    private Song MapAmazonTrackMetadataToSong(string trackAsin, AmazonMusicTrackMetadata meta)
    {
        var year = ParseAmazonYear(meta.Year, meta.Date);
        var coverUrl = ResolveAmazonCoverUrl(meta.CoverCdn ?? meta.Cover);
        var contributors = new List<string>();
        if (!string.IsNullOrEmpty(meta.Composer)) contributors.Add(meta.Composer);

        return new Song
        {
            Title = meta.Title ?? "",
            Artist = meta.Artist ?? "",
            Artists = !string.IsNullOrEmpty(meta.Artist)
                ? new List<Artist> { new Artist { Id = "", Name = meta.Artist, IsLocal = false, ExternalProvider = "squidwtf" } }
                : new List<Artist>(),
            Album = meta.Album ?? "",
            AlbumId = !string.IsNullOrEmpty(meta.AlbumAsin) ? $"ext-squidwtf-album-{meta.AlbumAsin}" : null,
            AlbumArtist = meta.AlbumArtist,
            Track = ParseAmazonJsonElementAsInt(meta.TrackNumber),
            DiscNumber = ParseAmazonJsonElementAsInt(meta.DiscNumber),
            TotalTracks = ParseAmazonJsonElementAsInt(meta.TrackTotal),
            Year = year,
            Genre = meta.Genre,
            Isrc = meta.Isrc,
            Copyright = meta.Copyright,
            Contributors = contributors,
            CoverArtUrl = coverUrl,
            CoverArtUrlLarge = coverUrl,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = trackAsin
        };
    }

    private Song MapAmazonQueueItemToSong(AmazonMusicQueueItem item)
    {
        var year = ParseAmazonYear(item.Year, item.Date);
        var coverUrl = ResolveAmazonCoverUrl(item.Cover ?? item.Thumbnail);
        if (coverUrl != null && item.Asin != null) CacheCoverUrl(item.Asin, coverUrl);

        return new Song
        {
            Title = item.Title ?? "",
            Artist = item.AlbumArtist ?? "",
            Artists = !string.IsNullOrEmpty(item.AlbumArtist)
                ? new List<Artist> { new Artist { Id = "", Name = item.AlbumArtist, IsLocal = false, ExternalProvider = "squidwtf" } }
                : new List<Artist>(),
            AlbumArtist = item.AlbumArtist,
            Track = ParseAmazonJsonElementAsInt(item.TrackNumber),
            DiscNumber = ParseAmazonJsonElementAsInt(item.DiscNumber),
            Year = year,
            CoverArtUrl = coverUrl,
            CoverArtUrlLarge = coverUrl,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = item.Asin!
        };
    }

    private Album MapAmazonSearchAlbumToAlbum(AmazonMusicSearchAlbum album)
    {
        var externalId = album.Asin!;
        var artistName = album.PrimaryArtistName ?? album.ArtistName ?? album.AlbumArtistName ?? "";
        var coverUrl = ResolveAmazonCoverUrl(album.Image ?? album.Cover);

        return new Album
        {
            Id = $"ext-squidwtf-album-{externalId}",
            Title = album.Title ?? "",
            Artist = artistName,
            ArtistId = null,
            CoverArtUrl = coverUrl,
            CoverArtUrlLarge = coverUrl,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

    #endregion

    #region Tidal Backend Methods

    private async Task<List<Song>> SearchSongsTidalAsync(string query, int limit)
    {
        var response = await SendTidalRequestAsync($"/search/?s={Uri.EscapeDataString(query)}");
        
        if (response == null) return new List<Song>();
        
        var dataResponse = JsonSerializer.Deserialize<TidalDataResponse<TidalTrack>>(response);
        if (dataResponse?.Data?.Items == null) return new List<Song>();
        
        var songs = new List<Song>();
        foreach (var track in dataResponse.Data.Items.Take(limit))
        {
            var song = MapTidalTrackToSong(track);
            if (ShouldIncludeSong(song))
            {
                songs.Add(song);
            }
        }

        // Filter duplicates
        songs = songs
            .DistinctBy(s => new { s.Title, s.Artist, s.Album, s.Duration, s.ReleaseDate }).ToList();
        
        return songs;
    }

    private async Task<List<Album>> SearchAlbumsTidalAsync(string query, int limit)
    {
        var response = await SendTidalRequestAsync($"/search/?al={Uri.EscapeDataString(query)}");
        
        if (response == null) return new List<Album>();
        
        var dataResponse = JsonSerializer.Deserialize<TidalNestedSearchResponse>(response);
        if (dataResponse?.Data?.Albums?.Items == null) return new List<Album>();

        // Filter duplicates
        var albums = dataResponse.Data.Albums.Items
            .DistinctBy(a => new { a.Title, a.Artist?.Name, a.NumberOfTracks, a.ReleaseDate }).ToList();
        
        return albums
            .Take(limit)
            .Select(MapTidalAlbumToAlbum)
            .ToList();
    }

    private async Task<List<Artist>> SearchArtistsTidalAsync(string query, int limit)
    {
        var response = await SendTidalRequestAsync($"/search/?a={Uri.EscapeDataString(query)}");
        
        if (response == null) return new List<Artist>();
        
        var dataResponse = JsonSerializer.Deserialize<TidalNestedSearchResponse>(response);
        if (dataResponse?.Data?.Artists?.Items == null) return new List<Artist>();
        
        return dataResponse.Data.Artists.Items
            .Take(limit)
            .Select(MapTidalArtistToArtist)
            .ToList();
    }

    private async Task<List<ExternalPlaylist>> SearchPlaylistsTidalAsync(string query, int limit)
    {
        var response = await SendTidalRequestAsync($"/search/?p={Uri.EscapeDataString(query)}");
        
        if (response == null)
        {
            _logger.LogWarning("Tidal playlist search returned null response for query: {Query}", query);
            return new List<ExternalPlaylist>();
        }
        
        _logger.LogDebug("Tidal playlist search response length: {Length} for query: {Query}", response.Length, query);
        
        try
        {
            var dataResponse = JsonSerializer.Deserialize<TidalNestedSearchResponse>(response);
            
            if (dataResponse?.Data?.Playlists?.Items == null)
            {
                _logger.LogWarning("Tidal playlist search - parsed but no playlists found. Data null: {DataNull}, Playlists null: {PlaylistsNull}", 
                    dataResponse?.Data == null, dataResponse?.Data?.Playlists == null);
                return new List<ExternalPlaylist>();
            }
            
            _logger.LogInformation("Tidal playlist search found {Count} playlists for query: {Query}", 
                dataResponse.Data.Playlists.Items.Count, query);
            
            return dataResponse.Data.Playlists.Items
                .Take(limit)
                .Select(MapTidalPlaylistToExternalPlaylist)
                .ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Tidal playlist response for query: {Query}", query);
            return new List<ExternalPlaylist>();
        }
    }

    private async Task<Song?> GetSongTidalAsync(string trackId)
    {
        var response = await SendTidalRequestAsync($"/info/?id={trackId}");
        
        if (response == null) return null;
        
        var trackInfoWrapper = JsonSerializer.Deserialize<TidalTrackInfoResponseWrapper>(response);
        if (trackInfoWrapper?.Data == null) return null;
        
        return MapTidalTrackInfoToSong(trackInfoWrapper.Data);
    }

    private async Task<Album?> GetAlbumTidalAsync(string albumId)
    {
        // Use dedicated /album/ endpoint for fetching album by ID
        var response = await SendTidalRequestAsync($"/album/?id={albumId}");
        
        if (response == null) return null;
        var albumResponse = JsonSerializer.Deserialize<TidalAlbumResponse>(response);
        var albumData = albumResponse?.Data;
        
        if (albumData == null) return null;
        
        var album = MapTidalAlbumDataToAlbum(albumData);
        
        // Add tracks from items
        if (albumData.Items != null)
        {
            foreach (var item in albumData.Items)
            {
                if (item.Type == "track" && item.Item != null)
                {
                    var song = MapTidalTrackToSong(item.Item);
                    song.Album = album.Title;
                    song.AlbumId = album.Id;
                    song.AlbumArtist = album.Artist;
                    song.Year ??= album.Year;
                    song.Genre ??= album.Genre;
                    song.TotalTracks ??= album.SongCount;
                    song.ReleaseType ??= album.ReleaseType;
                    
                    // Use album cover for tracks if track doesn't have one
                    if (string.IsNullOrEmpty(song.CoverArtUrl))
                    {
                        song.CoverArtUrl = album.CoverArtUrl;
                    }
                    if (string.IsNullOrEmpty(song.CoverArtUrlLarge))
                    {
                        song.CoverArtUrlLarge = album.CoverArtUrlLarge;
                    }
                    
                    if (ShouldIncludeSong(song))
                    {
                        album.Songs.Add(song);
                    }
                }
            }
        }
        
        return album;
    }

    private async Task<Artist?> GetArtistTidalAsync(string artistId)
    {
        // Use dedicated /artist/ endpoint for fetching artist by ID
        var response = await SendTidalRequestAsync($"/artist/?id={artistId}");
        
        if (response == null) return null;
        
        var artistResponse = JsonSerializer.Deserialize<TidalArtistResponse>(response);
        
        if (artistResponse?.Artist == null) return null;
        
        return MapTidalArtistDataToArtist(artistResponse);
    }

    private async Task<List<Album>> GetArtistAlbumsTidalAsync(string artistId)
    {
        var response = await SendTidalRequestAsync($"/artist/?f={artistId}&skip_tracks=true");
        if (response == null) return new List<Album>();
        
        var dataResponse = JsonSerializer.Deserialize<TidalArtistAlbumsResponseWrapper>(response);
        if (dataResponse?.Albums?.Items == null) return new List<Album>();

        // Filter duplicates
        var albums = dataResponse.Albums.Items
            .DistinctBy(a => new { a.Title, a.Artist?.Name, a.NumberOfTracks, a.ReleaseDate }).ToList();
        
        // Filter albums that have this artist as main artist
        return albums
            .Select(MapTidalAlbumToAlbum)
            .ToList();
    }

    private async Task<ExternalPlaylist?> GetPlaylistTidalAsync(string playlistUuid)
    {
        var response = await SendTidalRequestAsync($"/playlist/?id={playlistUuid}");
        
        if (response == null)
        {
            _logger.LogWarning("Tidal playlist fetch returned null for UUID: {PlaylistUuid}", playlistUuid);
            return null;
        }
        
        try
        {
            var playlistResponse = JsonSerializer.Deserialize<TidalPlaylistResponse>(response);
            
            if (playlistResponse?.Playlist == null)
            {
                _logger.LogWarning("Tidal playlist response has no playlist data for UUID: {PlaylistUuid}", playlistUuid);
                return null;
            }
            
            return MapTidalPlaylistToExternalPlaylist(playlistResponse.Playlist);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Tidal playlist response for UUID: {PlaylistUuid}", playlistUuid);
            return null;
        }
    }

    private async Task<List<Song>> GetPlaylistTracksTidalAsync(string playlistUuid)
    {
        var response = await SendTidalRequestAsync($"/playlist/?id={playlistUuid}");
        
        if (response == null)
        {
            _logger.LogWarning("Tidal playlist tracks fetch returned null for UUID: {PlaylistUuid}", playlistUuid);
            return new List<Song>();
        }
        
        try
        {
            var playlistResponse = JsonSerializer.Deserialize<TidalPlaylistResponse>(response);
            
            if (playlistResponse?.Items == null)
            {
                _logger.LogWarning("Tidal playlist response has no items for UUID: {PlaylistUuid}", playlistUuid);
                return new List<Song>();
            }
            
            _logger.LogInformation("Tidal playlist has {Count} items for UUID: {PlaylistUuid}", 
                playlistResponse.Items.Count, playlistUuid);
            
            var songs = new List<Song>();
            foreach (var item in playlistResponse.Items)
            {
                if (item.Type == "track" && item.Item != null)
                {
                    var song = MapTidalTrackToSong(item.Item);
                    if (ShouldIncludeSong(song))
                    {
                        song.Track = songs.Count + 1;
                        songs.Add(song);
                    }
                }
            }
            
            return songs;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Tidal playlist tracks response for UUID: {PlaylistUuid}", playlistUuid);
            return new List<Song>();
        }
    }

    /// <summary>
    /// Sends a request to the Tidal API with automatic instance failover
    /// </summary>
    /// <param name="path">Relative path (e.g., "/search/?s=query")</param>
    private async Task<string?> SendTidalRequestAsync(string path)
    {
        try
        {
            var response = await _instanceManager.SendWithFailoverAsync(baseUrl =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{path}");
                request.Headers.Add(TidalClientHeader, TidalClientValue);
                return request;
            });
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tidal API returned {StatusCode} for {Path}", response.StatusCode, path);
                return null;
            }
            
            return await response.Content.ReadAsStringAsync();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "All Tidal instances failed for {Path}", path);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Tidal request to {Path}", path);
            return null;
        }
    }

    #endregion

    #region Mapping Methods - Qobuz

    private Song MapQobuzTrackToSong(QobuzTrack track)
    {
        var externalId = track.Id.ToString();
        
        // Parse year from release date
        int? year = null;
        var releaseDate = track.ReleaseDateOriginal ?? track.Album?.ReleaseDateOriginal;
        if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4)
        {
            if (int.TryParse(releaseDate.Substring(0, 4), out var y))
            {
                year = y;
            }
        }
        // Fallback to album released_at timestamp
        if (year == null && track.Album?.ReleasedAt.HasValue == true)
        {
            var dateTime = DateTimeOffset.FromUnixTimeSeconds(track.Album.ReleasedAt.Value).DateTime;
            year = dateTime.Year;
        }
        
        // Get composers from composer field
        var contributors = new List<string>();
        if (track.Composer != null && !string.IsNullOrEmpty(track.Composer.Name))
        {
            contributors.Add(track.Composer.Name);
        }
        
        var performerName = track.Performer?.Name ?? "";
        var performerArtistId = track.Performer != null ? $"ext-squidwtf-artist-{track.Performer.Id}" : null;

        return new Song
        {
            Title = track.Title ?? "",
            Artist = performerName,
            Artists = !string.IsNullOrEmpty(performerName)
                ? new List<Artist> { new Artist { Id = performerArtistId ?? "", Name = performerName, IsLocal = false, ExternalProvider = "squidwtf", ExternalId = track.Performer?.Id.ToString() } }
                : new List<Artist>(),
            ArtistId = performerArtistId,
            Album = track.Album?.Title ?? "",
            AlbumId = track.Album != null ? $"ext-squidwtf-album-{track.Album.Id}" : null,
            Duration = track.Duration,
            Track = track.TrackNumber,
            DiscNumber = track.MediaNumber > 0 ? track.MediaNumber : null,
            Year = year,
            Genre = track.Album?.Genre?.Name,
            Isrc = track.Isrc,
            Copyright = track.Copyright ?? track.Album?.Copyright,
            Contributors = contributors,
            TotalTracks = track.Album?.TracksCount,
            CoverArtUrl = track.Album?.Image?.Thumbnail ?? track.Album?.Image?.Small,
            CoverArtUrlLarge = track.Album?.Image?.Large,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId,
            ExplicitContentLyrics = track.ParentalWarning ? 1 : 0
        };
    }

    private Album MapQobuzAlbumToAlbum(QobuzAlbum album)
    {
        var externalId = album.Id ?? "";
        
        int? year = null;
        if (album.ReleasedAt.HasValue)
        {
            var dateTime = DateTimeOffset.FromUnixTimeSeconds(album.ReleasedAt.Value).DateTime;
            year = dateTime.Year;
        }
        
        return new Album
        {
            Id = $"ext-squidwtf-album-{externalId}",
            Title = album.Title ?? "",
            Artist = album.Artist?.Name ?? "",
            ArtistId = album.Artist != null ? $"ext-squidwtf-artist-{album.Artist.Id}" : null,
            Year = year,
            SongCount = album.TracksCount,
            CoverArtUrl = album.Image?.Small ?? album.Image?.Thumbnail,
            CoverArtUrlLarge = album.Image?.Large,
            Genre = album.Genre?.Name,
            ReleaseType = album.ReleaseType,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

    private Artist MapQobuzArtistToArtist(QobuzArtist artist)
    {
        var externalId = artist.Id.ToString();
        
        return new Artist
        {
            Id = $"ext-squidwtf-artist-{externalId}",
            Name = artist.Name ?? "",
            ImageUrl = artist.Image?.Large ?? artist.Image?.Thumbnail,
            AlbumCount = artist.AlbumsCount > 0 ? artist.AlbumsCount : null,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

    #endregion

    #region Mapping Methods - Tidal

    private Song MapTidalTrackToSong(TidalTrack track)
    {
        var externalId = track.Id.ToString();
        
        // Parse year from album release date
        int? year = null;
        if (!string.IsNullOrEmpty(track.Album?.ReleaseDate) && track.Album.ReleaseDate.Length >= 4)
        {
            if (int.TryParse(track.Album.ReleaseDate.Substring(0, 4), out var y))
            {
                year = y;
            }
        }
        
        var artists = track.Artists?
            .Where(a => !string.IsNullOrEmpty(a.Name))
            .Select(MapTidalArtistToArtist)
            .ToList() ?? new List<Artist>();

        // Ensure main artist is present (first) when the artists array is empty
        if (artists.Count == 0 && track.Artist != null && !string.IsNullOrEmpty(track.Artist.Name))
            artists.Add(MapTidalArtistToArtist(track.Artist));

        var mainArtistName = track.Artist?.Name ?? (artists.FirstOrDefault()?.Name ?? "");

        var title = track.Title ?? "";
        if (!string.IsNullOrEmpty(track.Version))
            title += $" ({track.Version})";

        return new Song
        {
            Title = title,
            Artist = mainArtistName,
            Artists = artists,
            ArtistId = track.Artist != null
                ? $"ext-squidwtf-artist-{track.Artist.Id}"
                : artists.FirstOrDefault()?.Id,
            Album = track.Album?.Title ?? "",
            AlbumId = track.Album != null ? $"ext-squidwtf-album-{track.Album.Id}" : null,
            Duration = track.Duration,
            Track = track.TrackNumber,
            DiscNumber = track.VolumeNumber,
            Year = year,
            Isrc = track.Isrc,
            Bpm = track.Bpm,
            ReleaseType = track.Album?.Type,
            Copyright = track.Copyright,
            TotalTracks = track.Album?.NumberOfTracks,
            CoverArtUrl = GetTidalCoverUrl(track.Album?.Cover, "320x320"),
            CoverArtUrlLarge = GetTidalCoverUrl(track.Album?.Cover, "1280x1280"),
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId,
            ExplicitContentLyrics = track.Explicit ? 1 : 0
        };
    }

    private Song MapTidalTrackInfoToSong(TidalTrackInfoResponse track)
    {
        var externalId = track.Id.ToString();
        
        // Parse year from album release date
        int? year = null;
        if (!string.IsNullOrEmpty(track.Album?.ReleaseDate) && track.Album.ReleaseDate.Length >= 4)
        {
            if (int.TryParse(track.Album.ReleaseDate.Substring(0, 4), out var y))
            {
                year = y;
            }
        }
        
        var artists = track.Artists?
            .Where(a => !string.IsNullOrEmpty(a.Name))
            .Select(MapTidalArtistToArtist)
            .ToList() ?? new List<Artist>();

        // Ensure main artist is present (first) when the artists array is empty
        if (artists.Count == 0 && track.Artist != null && !string.IsNullOrEmpty(track.Artist.Name))
            artists.Add(MapTidalArtistToArtist(track.Artist));

        var mainArtistName = track.Artist?.Name ?? (artists.FirstOrDefault()?.Name ?? "");

        return new Song
        {
            Title = track.Title ?? "",
            Artist = mainArtistName,
            Artists = artists,
            ArtistId = track.Artist != null
                ? $"ext-squidwtf-artist-{track.Artist.Id}"
                : artists.FirstOrDefault()?.Id,
            Album = track.Album?.Title ?? "",
            AlbumId = track.Album != null ? $"ext-squidwtf-album-{track.Album.Id}" : null,
            Duration = track.Duration,
            Track = track.TrackNumber,
            DiscNumber = track.VolumeNumber,
            Year = year,
            Isrc = track.Isrc,
            Bpm = track.Bpm,
            Copyright = track.Copyright,
            TotalTracks = track.Album?.NumberOfTracks,
            CoverArtUrl = GetTidalCoverUrl(track.Album?.Cover, "320x320"),
            CoverArtUrlLarge = GetTidalCoverUrl(track.Album?.Cover, "1280x1280"),
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId,
            ExplicitContentLyrics = track.Explicit ? 1 : 0
        };
    }

    private Album MapTidalAlbumToAlbum(TidalAlbum album)
    {
        var externalId = album.Id.ToString();
        
        int? year = null;
        if (!string.IsNullOrEmpty(album.ReleaseDate) && album.ReleaseDate.Length >= 4)
        {
            if (int.TryParse(album.ReleaseDate.Substring(0, 4), out var y))
            {
                year = y;
            }
        }
        
        // Get main artist from singular field or first in artists array
        var mainArtist = album.Artist ?? album.Artists?.FirstOrDefault();
        
        return new Album
        {
            Id = $"ext-squidwtf-album-{externalId}",
            Title = album.Title ?? "",
            Artist = mainArtist?.Name ?? "",
            ArtistId = mainArtist != null ? $"ext-squidwtf-artist-{mainArtist.Id}" : null,
            Year = year,
            SongCount = album.NumberOfTracks,
            ReleaseType = album.Type,
            CoverArtUrl = GetTidalCoverUrl(album.Cover, "320x320"),
            CoverArtUrlLarge = GetTidalCoverUrl(album.Cover, "1280x1280"),
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

    private Artist MapTidalArtistToArtist(TidalArtist artist)
    {
        var externalId = artist.Id.ToString();
        
        return new Artist
        {
            Id = $"ext-squidwtf-artist-{externalId}",
            Name = artist.Name ?? "",
            ImageUrl = GetTidalImageUrl(artist.Picture),
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

    /// <summary>
    /// Maps TidalAlbumData (from /album/ endpoint) to Album domain model
    /// </summary>
    private Album MapTidalAlbumDataToAlbum(TidalAlbumData albumData)
    {
        var externalId = albumData.Id.ToString();
        
        int? year = null;
        if (!string.IsNullOrEmpty(albumData.ReleaseDate) && albumData.ReleaseDate.Length >= 4)
        {
            if (int.TryParse(albumData.ReleaseDate.Substring(0, 4), out var y))
            {
                year = y;
            }
        }
        
        // Get main artist from singular field or first in artists array
        var mainArtist = albumData.Artist ?? albumData.Artists?.FirstOrDefault();
        
        return new Album
        {
            Id = $"ext-squidwtf-album-{externalId}",
            Title = albumData.Title ?? "",
            Artist = mainArtist?.Name ?? "",
            ArtistId = mainArtist != null ? $"ext-squidwtf-artist-{mainArtist.Id}" : null,
            Year = year,
            SongCount = albumData.NumberOfTracks,
            ReleaseType = albumData.Type,
            CoverArtUrl = GetTidalCoverUrl(albumData.Cover, "320x320"),
            CoverArtUrlLarge = GetTidalCoverUrl(albumData.Cover, "1280x1280"),
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

    /// <summary>
    /// Maps TidalArtistResponse (from /artist/ endpoint) to Artist domain model
    /// </summary>
    private Artist MapTidalArtistDataToArtist(TidalArtistResponse artistResponse)
    {
        var artistData = artistResponse.Artist!;
        var externalId = artistData.Id.ToString();
        
        // Use the cover URL from the response if available, otherwise build from picture ID
        string? imageUrl = artistResponse.Cover?.Image750;
        if (string.IsNullOrEmpty(imageUrl))
        {
            imageUrl = GetTidalImageUrl(artistData.Picture);
        }
        
        return new Artist
        {
            Id = $"ext-squidwtf-artist-{externalId}",
            Name = artistData.Name ?? "",
            ImageUrl = imageUrl,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

    private ExternalPlaylist MapTidalPlaylistToExternalPlaylist(TidalPlaylist playlist)
    {
        return new ExternalPlaylist
        {
            Id = Common.PlaylistIdHelper.CreatePlaylistId("squidwtf", playlist.Uuid ?? ""),
            Name = playlist.Title ?? "",
            CuratorName = playlist.Creator?.Name,
            Provider = "squidwtf",
            ExternalId = playlist.Uuid ?? "",
            TrackCount = playlist.NumberOfTracks,
            Duration = playlist.Duration,
            CoverUrl = GetTidalCoverUrl(playlist.SquareImage ?? playlist.Image, "320x320")
        };
    }

    private static string? GetTidalCoverUrl(string? coverId, string size = "320x320")
    {
        if (string.IsNullOrEmpty(coverId)) return null;
        
        // Tidal cover IDs need dashes replaced with slashes
        var formattedId = coverId.Replace("-", "/");
        return $"https://resources.tidal.com/images/{formattedId}/{size}.jpg";
    }

    private static string? GetTidalImageUrl(string? pictureId)
    {
        if (string.IsNullOrEmpty(pictureId)) return null;
        
        var formattedId = pictureId.Replace("-", "/");
        return $"https://resources.tidal.com/images/{formattedId}/320x320.jpg";
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Determines whether a song should be included based on the explicit content filter setting
    /// </summary>
    private bool ShouldIncludeSong(Song song)
    {
        if (song.ExplicitContentLyrics == null)
            return true;
        
        return _subsonicSettings.ExplicitFilter switch
        {
            ExplicitFilter.All => true,
            ExplicitFilter.ExplicitOnly => song.ExplicitContentLyrics != 3,
            ExplicitFilter.CleanOnly => song.ExplicitContentLyrics != 1,
            _ => true
        };
    }

    #endregion
}
