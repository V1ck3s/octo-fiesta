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
/// Supports both Qobuz and Tidal backends
/// </summary>
public class SquidWTFMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly SquidWTFSettings _settings;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly SquidWTFInstanceManager _instanceManager;
    private readonly ILogger<SquidWTFMetadataService> _logger;
    
    // API endpoints
    private const string QobuzBaseUrl = "https://qobuz.squid.wtf";
    
    // Required headers
    private const string QobuzCountryHeader = "Token-Country";
    private const string QobuzCountryValue = "US";
    private const string TidalClientHeader = "x-client";
    private const string TidalClientValue = "BiniLossless/v3.4";
    
    private bool IsQobuzSource => _settings.Source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase);

    public SquidWTFMetadataService(
        IHttpClientFactory httpClientFactory, 
        IOptions<SquidWTFSettings> settings,
        IOptions<SubsonicSettings> subsonicSettings,
        SquidWTFInstanceManager instanceManager,
        ILogger<SquidWTFMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _subsonicSettings = subsonicSettings.Value;
        _instanceManager = instanceManager;
        _logger = logger;
    }

    #region IMusicMetadataService Implementation

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        try
        {
            if (IsQobuzSource)
            {
                return await SearchSongsQobuzAsync(query, limit);
            }
            else
            {
                return await SearchSongsTidalAsync(query, limit);
            }
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
            {
                return await SearchAlbumsQobuzAsync(query, limit);
            }
            else
            {
                return await SearchAlbumsTidalAsync(query, limit);
            }
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
            {
                return await SearchArtistsQobuzAsync(query, limit);
            }
            else
            {
                return await SearchArtistsTidalAsync(query, limit);
            }
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
        
        return new SearchResult
        {
            Songs = await songsTask,
            Albums = await albumsTask,
            Artists = await artistsTask
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return null;
        
        try
        {
            if (IsQobuzSource)
            {
                return await GetSongQobuzAsync(externalId);
            }
            else
            {
                return await GetSongTidalAsync(externalId);
            }
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
            {
                return await GetAlbumQobuzAsync(externalId);
            }
            else
            {
                return await GetAlbumTidalAsync(externalId);
            }
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
            {
                return await GetArtistQobuzAsync(externalId);
            }
            else
            {
                return await GetArtistTidalAsync(externalId);
            }
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
            {
                return await GetArtistAlbumsQobuzAsync(externalId);
            }
            else
            {
                return await GetArtistAlbumsTidalAsync(externalId);
            }
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
            // Only Tidal supports playlist search
            if (!IsQobuzSource)
            {
                return await SearchPlaylistsTidalAsync(query, limit);
            }
            
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
            // Only Tidal supports playlist fetching
            if (!IsQobuzSource)
            {
                return await GetPlaylistTidalAsync(externalId);
            }
            
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
            // Only Tidal supports playlist tracks
            if (!IsQobuzSource)
            {
                return await GetPlaylistTracksTidalAsync(externalId);
            }
            
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
        
        return songs;
    }

    private async Task<List<Album>> SearchAlbumsTidalAsync(string query, int limit)
    {
        var response = await SendTidalRequestAsync($"/search/?al={Uri.EscapeDataString(query)}");
        
        if (response == null) return new List<Album>();
        
        var dataResponse = JsonSerializer.Deserialize<TidalNestedSearchResponse>(response);
        if (dataResponse?.Data?.Albums?.Items == null) return new List<Album>();
        
        return dataResponse.Data.Albums.Items
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
        // Search for albums by artist name to get their discography
        // First get the artist to get their name
        var artist = await GetArtistTidalAsync(artistId);
        if (artist == null) return new List<Album>();
        
        // Search for albums by artist name
        var response = await SendTidalRequestAsync($"/search/?al={Uri.EscapeDataString(artist.Name)}");
        
        if (response == null) return new List<Album>();
        
        var dataResponse = JsonSerializer.Deserialize<TidalNestedSearchResponse>(response);
        if (dataResponse?.Data?.Albums?.Items == null) return new List<Album>();
        
        // Filter albums that have this artist as main artist
        return dataResponse.Data.Albums.Items
            .Where(a => a.Artists?.Any(ar => ar.Id.ToString() == artistId) == true ||
                       a.Artist?.Id.ToString() == artistId)
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
        
        return new Song
        {
            Id = $"ext-squidwtf-song-{externalId}",
            Title = track.Title ?? "",
            Artist = track.Performer?.Name ?? "",
            ArtistId = track.Performer != null ? $"ext-squidwtf-artist-{track.Performer.Id}" : null,
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
        
        return new Song
        {
            Id = $"ext-squidwtf-song-{externalId}",
            Title = track.Title ?? "",
            Artist = track.Artist?.Name ?? (track.Artists?.FirstOrDefault()?.Name ?? ""),
            ArtistId = track.Artist != null ? $"ext-squidwtf-artist-{track.Artist.Id}" : null,
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
        
        return new Song
        {
            Id = $"ext-squidwtf-song-{externalId}",
            Title = track.Title ?? "",
            Artist = track.Artist?.Name ?? (track.Artists?.FirstOrDefault()?.Name ?? ""),
            ArtistId = track.Artist != null ? $"ext-squidwtf-artist-{track.Artist.Id}" : null,
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
        
        return new Album
        {
            Id = $"ext-squidwtf-album-{externalId}",
            Title = album.Title ?? "",
            Artist = album.Artist?.Name ?? (album.Artists?.FirstOrDefault()?.Name ?? ""),
            ArtistId = album.Artist != null ? $"ext-squidwtf-artist-{album.Artist.Id}" : null,
            Year = year,
            SongCount = album.NumberOfTracks,
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
        
        return new Album
        {
            Id = $"ext-squidwtf-album-{externalId}",
            Title = albumData.Title ?? "",
            Artist = albumData.Artist?.Name ?? (albumData.Artists?.FirstOrDefault()?.Name ?? ""),
            ArtistId = albumData.Artist != null ? $"ext-squidwtf-artist-{albumData.Artist.Id}" : null,
            Year = year,
            SongCount = albumData.NumberOfTracks,
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
