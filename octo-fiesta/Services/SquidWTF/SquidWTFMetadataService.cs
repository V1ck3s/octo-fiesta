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
    private readonly ILogger<SquidWTFMetadataService> _logger;
    
    // API endpoints
    private const string QobuzBaseUrl = "https://qobuz.squid.wtf";
    private const string TidalBaseUrl = "https://triton.squid.wtf";
    
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
        ILogger<SquidWTFMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _subsonicSettings = subsonicSettings.Value;
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

    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        // Not implemented for SquidWTF
        return Task.FromResult<ExternalPlaylist?>(null);
    }

    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        // Not implemented for SquidWTF
        return Task.FromResult(new List<Song>());
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
        var url = $"{QobuzBaseUrl}/api/get-artist?artist_id={artistId}";
        var response = await SendQobuzRequestAsync(url);
        
        if (response == null) return new List<Album>();
        
        var artistResponse = JsonSerializer.Deserialize<QobuzArtistResponse>(response);
        if (artistResponse?.Data?.Albums?.Items == null) return new List<Album>();
        
        return artistResponse.Data.Albums.Items
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
        var url = $"{TidalBaseUrl}/search/?s={Uri.EscapeDataString(query)}";
        var response = await SendTidalRequestAsync(url);
        
        if (response == null) return new List<Song>();
        
        var searchResponse = JsonSerializer.Deserialize<TidalSearchResponse>(response);
        if (searchResponse?.Tracks == null) return new List<Song>();
        
        var songs = new List<Song>();
        foreach (var track in searchResponse.Tracks.Take(limit))
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
        var url = $"{TidalBaseUrl}/search/?al={Uri.EscapeDataString(query)}";
        var response = await SendTidalRequestAsync(url);
        
        if (response == null) return new List<Album>();
        
        var searchResponse = JsonSerializer.Deserialize<TidalSearchResponse>(response);
        if (searchResponse?.Albums == null) return new List<Album>();
        
        return searchResponse.Albums
            .Take(limit)
            .Select(MapTidalAlbumToAlbum)
            .ToList();
    }

    private async Task<List<Artist>> SearchArtistsTidalAsync(string query, int limit)
    {
        var url = $"{TidalBaseUrl}/search/?a={Uri.EscapeDataString(query)}";
        var response = await SendTidalRequestAsync(url);
        
        if (response == null) return new List<Artist>();
        
        var searchResponse = JsonSerializer.Deserialize<TidalSearchResponse>(response);
        if (searchResponse?.Artists == null) return new List<Artist>();
        
        return searchResponse.Artists
            .Take(limit)
            .Select(MapTidalArtistToArtist)
            .ToList();
    }

    private async Task<List<ExternalPlaylist>> SearchPlaylistsTidalAsync(string query, int limit)
    {
        var url = $"{TidalBaseUrl}/search/?p={Uri.EscapeDataString(query)}";
        var response = await SendTidalRequestAsync(url);
        
        if (response == null) return new List<ExternalPlaylist>();
        
        var searchResponse = JsonSerializer.Deserialize<TidalSearchResponse>(response);
        if (searchResponse?.Playlists == null) return new List<ExternalPlaylist>();
        
        return searchResponse.Playlists
            .Take(limit)
            .Select(MapTidalPlaylistToExternalPlaylist)
            .ToList();
    }

    private async Task<Song?> GetSongTidalAsync(string trackId)
    {
        var url = $"{TidalBaseUrl}/info/?id={trackId}";
        var response = await SendTidalRequestAsync(url);
        
        if (response == null) return null;
        
        var trackInfo = JsonSerializer.Deserialize<TidalTrackInfoResponse>(response);
        if (trackInfo == null) return null;
        
        return MapTidalTrackInfoToSong(trackInfo);
    }

    private async Task<Album?> GetAlbumTidalAsync(string albumId)
    {
        // Tidal search by album ID - search for the album and get details
        var url = $"{TidalBaseUrl}/search/?al={albumId}";
        var response = await SendTidalRequestAsync(url);
        
        if (response == null) return null;
        
        var searchResponse = JsonSerializer.Deserialize<TidalSearchResponse>(response);
        var albumData = searchResponse?.Albums?.FirstOrDefault(a => a.Id.ToString() == albumId);
        
        if (albumData == null) return null;
        
        var album = MapTidalAlbumToAlbum(albumData);
        
        // Add tracks if available
        if (albumData.Tracks != null)
        {
            foreach (var track in albumData.Tracks)
            {
                var song = MapTidalTrackToSong(track);
                song.Album = album.Title;
                song.AlbumId = album.Id;
                song.AlbumArtist = album.Artist;
                
                if (ShouldIncludeSong(song))
                {
                    album.Songs.Add(song);
                }
            }
        }
        
        return album;
    }

    private async Task<Artist?> GetArtistTidalAsync(string artistId)
    {
        var url = $"{TidalBaseUrl}/search/?a={artistId}";
        var response = await SendTidalRequestAsync(url);
        
        if (response == null) return null;
        
        var searchResponse = JsonSerializer.Deserialize<TidalSearchResponse>(response);
        var artistData = searchResponse?.Artists?.FirstOrDefault(a => a.Id.ToString() == artistId);
        
        if (artistData == null) return null;
        
        return MapTidalArtistToArtist(artistData);
    }

    private Task<List<Album>> GetArtistAlbumsTidalAsync(string artistId)
    {
        // Tidal doesn't have a direct artist albums endpoint via SquidWTF
        // Return empty list for now
        return Task.FromResult(new List<Album>());
    }

    private async Task<string?> SendTidalRequestAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(TidalClientHeader, TidalClientValue);
            
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tidal API returned {StatusCode} for {Url}", response.StatusCode, url);
                return null;
            }
            
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Tidal request to {Url}", url);
            return null;
        }
    }

    #endregion

    #region Mapping Methods - Qobuz

    private Song MapQobuzTrackToSong(QobuzTrack track)
    {
        var externalId = track.Id.ToString();
        
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
            CoverArtUrl = album.Image?.Thumbnail ?? album.Image?.Small,
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
            Isrc = track.Isrc,
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
            CoverUrl = playlist.Image
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
