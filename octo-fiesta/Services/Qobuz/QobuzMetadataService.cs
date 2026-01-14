using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace octo_fiesta.Services.Qobuz;

/// <summary>
/// Metadata service implementation using the Qobuz API
/// Uses user authentication token instead of email/password
/// </summary>
public class QobuzMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _settings;
    private readonly QobuzBundleService _bundleService;
    private readonly ILogger<QobuzMetadataService> _logger;
    private readonly string? _userAuthToken;
    private readonly string? _userId;
    
    private const string BaseUrl = "https://www.qobuz.com/api.json/0.2/";

    public QobuzMetadataService(
        IHttpClientFactory httpClientFactory, 
        IOptions<SubsonicSettings> settings,
        IOptions<QobuzSettings> qobuzSettings,
        QobuzBundleService bundleService,
        ILogger<QobuzMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _bundleService = bundleService;
        _logger = logger;
        
        var qobuzConfig = qobuzSettings.Value;
        _userAuthToken = qobuzConfig.UserAuthToken;
        _userId = qobuzConfig.UserId;
        
        // Set up default headers
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        try
        {
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}track/search?query={Uri.EscapeDataString(query)}&limit={limit}&app_id={appId}";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return new List<Song>();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            var songs = new List<Song>();
            if (result.RootElement.TryGetProperty("tracks", out var tracks) &&
                tracks.TryGetProperty("items", out var items))
            {
                foreach (var track in items.EnumerateArray())
                {
                    var song = ParseQobuzTrack(track);
                    if (ShouldIncludeSong(song))
                    {
                        songs.Add(song);
                    }
                }
            }
            
            return songs;
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
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}album/search?query={Uri.EscapeDataString(query)}&limit={limit}&app_id={appId}";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return new List<Album>();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            var albums = new List<Album>();
            if (result.RootElement.TryGetProperty("albums", out var albumsData) &&
                albumsData.TryGetProperty("items", out var items))
            {
                foreach (var album in items.EnumerateArray())
                {
                    albums.Add(ParseQobuzAlbum(album));
                }
            }
            
            return albums;
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
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}artist/search?query={Uri.EscapeDataString(query)}&limit={limit}&app_id={appId}";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return new List<Artist>();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            var artists = new List<Artist>();
            if (result.RootElement.TryGetProperty("artists", out var artistsData) &&
                artistsData.TryGetProperty("items", out var items))
            {
                foreach (var artist in items.EnumerateArray())
                {
                    artists.Add(ParseQobuzArtist(artist));
                }
            }
            
            return artists;
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
        if (externalProvider != "qobuz") return null;
        
        try
        {
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}track/get?track_id={externalId}&app_id={appId}";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            
            var json = await response.Content.ReadAsStringAsync();
            var track = JsonDocument.Parse(json).RootElement;
            
            if (track.TryGetProperty("error", out _)) return null;
            
            return ParseQobuzTrackFull(track);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get song {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "qobuz") return null;
        
        try
        {
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}album/get?album_id={externalId}&app_id={appId}";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            
            var json = await response.Content.ReadAsStringAsync();
            var albumElement = JsonDocument.Parse(json).RootElement;
            
            if (albumElement.TryGetProperty("error", out _)) return null;
            
            var album = ParseQobuzAlbum(albumElement);
            
            // Get album tracks
            if (albumElement.TryGetProperty("tracks", out var tracks) &&
                tracks.TryGetProperty("items", out var tracksData))
            {
                foreach (var track in tracksData.EnumerateArray())
                {
                    var song = ParseQobuzTrack(track);
                    
                    // Ensure album metadata is set (tracks in album response may not have full album object)
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get album {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "qobuz") return null;
        
        try
        {
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}artist/get?artist_id={externalId}&app_id={appId}";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            
            var json = await response.Content.ReadAsStringAsync();
            var artist = JsonDocument.Parse(json).RootElement;
            
            if (artist.TryGetProperty("error", out _)) return null;
            
            return ParseQobuzArtist(artist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artist {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "qobuz") return new List<Album>();
        
        try
        {
            var albums = new List<Album>();
            var appId = await _bundleService.GetAppIdAsync();
            int offset = 0;
            const int limit = 500;
            
            // Qobuz requires pagination for artist albums
            while (true)
            {
                var url = $"{BaseUrl}artist/get?artist_id={externalId}&app_id={appId}&limit={limit}&offset={offset}&extra=albums";
                
                var response = await GetWithAuthAsync(url);
                if (!response.IsSuccessStatusCode) break;
                
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonDocument.Parse(json);
                
                if (!result.RootElement.TryGetProperty("albums", out var albumsData) ||
                    !albumsData.TryGetProperty("items", out var items))
                {
                    break;
                }
                
                var itemsArray = items.EnumerateArray().ToList();
                if (itemsArray.Count == 0) break;
                
                foreach (var album in itemsArray)
                {
                    albums.Add(ParseQobuzAlbum(album));
                }
                
                // If we got less than the limit, we've reached the end
                if (itemsArray.Count < limit) break;
                
                offset += limit;
            }
            
            return albums;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artist albums for {ExternalId}", externalId);
            return new List<Album>();
        }
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        try
        {
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}playlist/search?query={Uri.EscapeDataString(query)}&limit={limit}&app_id={appId}";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return new List<ExternalPlaylist>();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            var playlists = new List<ExternalPlaylist>();
            if (result.RootElement.TryGetProperty("playlists", out var playlistsData) &&
                playlistsData.TryGetProperty("items", out var items))
            {
                foreach (var playlist in items.EnumerateArray())
                {
                    playlists.Add(ParseQobuzPlaylist(playlist));
                }
            }
            
            return playlists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search playlists for query: {Query}", query);
            return new List<ExternalPlaylist>();
        }
    }
    
    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "qobuz") return null;
        
        try
        {
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}playlist/get?playlist_id={externalId}&app_id={appId}";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            
            var json = await response.Content.ReadAsStringAsync();
            var playlistElement = JsonDocument.Parse(json).RootElement;
            
            if (playlistElement.TryGetProperty("error", out _)) return null;
            
            return ParseQobuzPlaylist(playlistElement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlist {ExternalId}", externalId);
            return null;
        }
    }
    
    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "qobuz") return new List<Song>();
        
        try
        {
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}playlist/get?playlist_id={externalId}&app_id={appId}&extra=tracks";
            
            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return new List<Song>();
            
            var json = await response.Content.ReadAsStringAsync();
            var playlistElement = JsonDocument.Parse(json).RootElement;
            
            if (playlistElement.TryGetProperty("error", out _)) return new List<Song>();
            
            var songs = new List<Song>();
            
            // Get playlist name for album field
            var playlistName = playlistElement.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() ?? "Unknown Playlist"
                : "Unknown Playlist";
            
            if (playlistElement.TryGetProperty("tracks", out var tracks) &&
                tracks.TryGetProperty("items", out var tracksData))
            {
                int trackIndex = 1;
                foreach (var track in tracksData.EnumerateArray())
                {
                    // For playlists, use the track's own artist (not a single album artist)
                    var song = ParseQobuzTrack(track);
                    
                    // Override album name to be the playlist name
                    song.Album = playlistName;
                    song.Track = trackIndex;
                    
                    if (ShouldIncludeSong(song))
                    {
                        songs.Add(song);
                    }
                    trackIndex++;
                }
            }
            
            return songs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlist tracks for {ExternalId}", externalId);
            return new List<Song>();
        }
    }
    
    private ExternalPlaylist ParseQobuzPlaylist(JsonElement playlist)
    {
        var externalId = GetIdAsString(playlist.GetProperty("id"));
        
        // Get curator/creator name
        string? curatorName = null;
        if (playlist.TryGetProperty("owner", out var owner) &&
            owner.TryGetProperty("name", out var ownerName))
        {
            curatorName = ownerName.GetString();
        }
        
        // Get creation date
        DateTime? createdDate = null;
        if (playlist.TryGetProperty("created_at", out var createdAtEl))
        {
            var timestamp = createdAtEl.GetInt64();
            createdDate = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
        }
        
        // Get cover URL from images
        string? coverUrl = null;
        if (playlist.TryGetProperty("images300", out var images300))
        {
            var imagesArray = images300.EnumerateArray().ToList();
            if (imagesArray.Count > 0)
            {
                coverUrl = imagesArray[0].GetString();
            }
        }
        else if (playlist.TryGetProperty("image_rectangle", out var imageRect))
        {
            var imagesArray = imageRect.EnumerateArray().ToList();
            if (imagesArray.Count > 0)
            {
                coverUrl = imagesArray[0].GetString();
            }
        }
        
        return new ExternalPlaylist
        {
            Id = Common.PlaylistIdHelper.CreatePlaylistId("qobuz", externalId),
            Name = playlist.TryGetProperty("name", out var name)
                ? name.GetString() ?? ""
                : "",
            Description = playlist.TryGetProperty("description", out var desc)
                ? desc.GetString()
                : null,
            CuratorName = curatorName,
            Provider = "qobuz",
            ExternalId = externalId,
            TrackCount = playlist.TryGetProperty("tracks_count", out var tracksCount)
                ? tracksCount.GetInt32()
                : 0,
            Duration = playlist.TryGetProperty("duration", out var duration)
                ? duration.GetInt32()
                : 0,
            CoverUrl = coverUrl,
            CreatedDate = createdDate
        };
    }

    /// <summary>
    /// Safely gets an ID value as a string, handling both number and string types from JSON
    /// </summary>
    private string GetIdAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetInt64().ToString(),
            JsonValueKind.String => element.GetString() ?? "",
            _ => ""
        };
    }

    /// <summary>
    /// Makes an HTTP GET request with Qobuz authentication headers
    /// </summary>
    private async Task<HttpResponseMessage> GetWithAuthAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        var appId = await _bundleService.GetAppIdAsync();
        request.Headers.Add("X-App-Id", appId);
        
        if (!string.IsNullOrEmpty(_userAuthToken))
        {
            request.Headers.Add("X-User-Auth-Token", _userAuthToken);
        }
        
        return await _httpClient.SendAsync(request);
    }

    private Song ParseQobuzTrack(JsonElement track)
    {
        var externalId = GetIdAsString(track.GetProperty("id"));
        
        var title = track.GetProperty("title").GetString() ?? "";
        
        // Add version to title if present (e.g., "Remastered", "Live")
        if (track.TryGetProperty("version", out var version))
        {
            var versionStr = version.GetString();
            if (!string.IsNullOrEmpty(versionStr))
            {
                title = $"{title} ({versionStr})";
            }
        }
        
        // For classical music, prepend work name
        if (track.TryGetProperty("work", out var work))
        {
            var workStr = work.GetString();
            if (!string.IsNullOrEmpty(workStr))
            {
                title = $"{workStr}: {title}";
            }
        }
        
        var performerName = track.TryGetProperty("performer", out var performer)
            ? performer.GetProperty("name").GetString() ?? ""
            : "";
        
        var albumTitle = track.TryGetProperty("album", out var album)
            ? album.GetProperty("title").GetString() ?? ""
            : "";
        
        var albumId = track.TryGetProperty("album", out var albumForId)
            ? $"ext-qobuz-album-{GetIdAsString(albumForId.GetProperty("id"))}"
            : null;
        
        // Get album artist
        var albumArtist = track.TryGetProperty("album", out var albumForArtist) &&
                          albumForArtist.TryGetProperty("artist", out var albumArtistEl)
            ? albumArtistEl.GetProperty("name").GetString()
            : performerName;
        
        return new Song
        {
            Id = $"ext-qobuz-song-{externalId}",
            Title = title,
            Artist = performerName,
            ArtistId = track.TryGetProperty("performer", out var performerForId)
                ? $"ext-qobuz-artist-{GetIdAsString(performerForId.GetProperty("id"))}"
                : null,
            Album = albumTitle,
            AlbumId = albumId,
            AlbumArtist = albumArtist,
            Duration = track.TryGetProperty("duration", out var duration)
                ? duration.GetInt32()
                : null,
            Track = track.TryGetProperty("track_number", out var trackNum)
                ? trackNum.GetInt32()
                : null,
            DiscNumber = track.TryGetProperty("media_number", out var mediaNum)
                ? mediaNum.GetInt32()
                : null,
            CoverArtUrl = GetCoverArtUrl(track),
            IsLocal = false,
            ExternalProvider = "qobuz",
            ExternalId = externalId
        };
    }

    private Song ParseQobuzTrackFull(JsonElement track)
    {
        var song = ParseQobuzTrack(track);
        
        // Add additional metadata for full track
        if (track.TryGetProperty("composer", out var composer) &&
            composer.TryGetProperty("name", out var composerName))
        {
            song.Contributors = new List<string> { composerName.GetString() ?? "" };
        }
        
        if (track.TryGetProperty("isrc", out var isrc))
        {
            song.Isrc = isrc.GetString();
        }
        
        if (track.TryGetProperty("copyright", out var copyright))
        {
            song.Copyright = FormatCopyright(copyright.GetString() ?? "");
        }
        
        // Get release date from album
        if (track.TryGetProperty("album", out var album))
        {
            if (album.TryGetProperty("release_date_original", out var releaseDate))
            {
                var dateStr = releaseDate.GetString();
                song.ReleaseDate = dateStr;
                
                if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4)
                {
                    if (int.TryParse(dateStr.Substring(0, 4), out var year))
                    {
                        song.Year = year;
                    }
                }
            }
            
            if (album.TryGetProperty("tracks_count", out var tracksCount))
            {
                song.TotalTracks = tracksCount.GetInt32();
            }
            
            if (album.TryGetProperty("genres_list", out var genres))
            {
                song.Genre = FormatGenres(genres);
            }
            
            // Get large cover art
            song.CoverArtUrlLarge = GetLargeCoverArtUrl(album);
        }
        
        return song;
    }

    private Album ParseQobuzAlbum(JsonElement album)
    {
        var externalId = GetIdAsString(album.GetProperty("id"));
        
        var title = album.GetProperty("title").GetString() ?? "";
        
        // Add version to title if present
        if (album.TryGetProperty("version", out var version))
        {
            var versionStr = version.GetString();
            if (!string.IsNullOrEmpty(versionStr))
            {
                title = $"{title} ({versionStr})";
            }
        }
        
        var artistName = album.TryGetProperty("artist", out var artist)
            ? artist.GetProperty("name").GetString() ?? ""
            : "";
        
        int? year = null;
        if (album.TryGetProperty("release_date_original", out var releaseDate))
        {
            var dateStr = releaseDate.GetString();
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4)
            {
                if (int.TryParse(dateStr.Substring(0, 4), out var y))
                {
                    year = y;
                }
            }
        }
        
        return new Album
        {
            Id = $"ext-qobuz-album-{externalId}",
            Title = title,
            Artist = artistName,
            ArtistId = album.TryGetProperty("artist", out var artistForId)
                ? $"ext-qobuz-artist-{GetIdAsString(artistForId.GetProperty("id"))}"
                : null,
            Year = year,
            SongCount = album.TryGetProperty("tracks_count", out var tracksCount)
                ? tracksCount.GetInt32()
                : null,
            CoverArtUrl = GetCoverArtUrl(album),
            Genre = album.TryGetProperty("genres_list", out var genres)
                ? FormatGenres(genres)
                : null,
            IsLocal = false,
            ExternalProvider = "qobuz",
            ExternalId = externalId
        };
    }

    private Artist ParseQobuzArtist(JsonElement artist)
    {
        var externalId = GetIdAsString(artist.GetProperty("id"));
        
        return new Artist
        {
            Id = $"ext-qobuz-artist-{externalId}",
            Name = artist.GetProperty("name").GetString() ?? "",
            ImageUrl = GetArtistImageUrl(artist),
            AlbumCount = artist.TryGetProperty("albums_count", out var albumsCount)
                ? albumsCount.GetInt32()
                : null,
            IsLocal = false,
            ExternalProvider = "qobuz",
            ExternalId = externalId
        };
    }

    /// <summary>
    /// Extracts cover art URL from track or album element
    /// </summary>
    private string? GetCoverArtUrl(JsonElement element)
    {
        // For tracks, get album image
        if (element.TryGetProperty("album", out var album))
        {
            element = album;
        }
        
        if (element.TryGetProperty("image", out var image))
        {
            // Prefer thumbnail (230x230), fallback to small
            if (image.TryGetProperty("thumbnail", out var thumbnail))
            {
                return thumbnail.GetString();
            }
            if (image.TryGetProperty("small", out var small))
            {
                return small.GetString();
            }
        }
        
        return null;
    }

    /// <summary>
    /// Gets large cover art URL (600x600 or original)
    /// </summary>
    private string? GetLargeCoverArtUrl(JsonElement album)
    {
        if (album.TryGetProperty("image", out var image) &&
            image.TryGetProperty("large", out var large))
        {
            var url = large.GetString();
            // Replace _600.jpg with _org.jpg for original quality
            return url?.Replace("_600.jpg", "_org.jpg");
        }
        
        return null;
    }

    /// <summary>
    /// Gets artist image URL
    /// </summary>
    private string? GetArtistImageUrl(JsonElement artist)
    {
        if (artist.TryGetProperty("image", out var image) &&
            image.TryGetProperty("large", out var large))
        {
            return large.GetString();
        }
        
        return null;
    }

    /// <summary>
    /// Formats Qobuz genre list into a readable string
    /// Example: ["Pop/Rock", "Pop/Rock→Rock"] becomes "Pop, Rock"
    /// </summary>
    private string FormatGenres(JsonElement genresList)
    {
        var genres = new List<string>();
        
        foreach (var genre in genresList.EnumerateArray())
        {
            var genreStr = genre.GetString();
            if (!string.IsNullOrEmpty(genreStr))
            {
                // Extract individual genres from paths like "Pop/Rock→Rock→Alternative"
                var parts = genreStr.Split(new[] { '/', '→' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (!genres.Contains(trimmed))
                    {
                        genres.Add(trimmed);
                    }
                }
            }
        }
        
        return string.Join(", ", genres);
    }

    /// <summary>
    /// Formats copyright string
    /// Replaces (P) with ℗ and (C) with ©
    /// </summary>
    private string FormatCopyright(string copyright)
    {
        return copyright
            .Replace("(P)", "℗")
            .Replace("(C)", "©");
    }

    /// <summary>
    /// Determines whether a song should be included based on the explicit content filter setting
    /// Note: Qobuz doesn't have the same explicit content tagging as Deezer, so this is a no-op for now
    /// </summary>
    private bool ShouldIncludeSong(Song song)
    {
        // Qobuz API doesn't expose explicit content flags in the same way as Deezer
        // We could implement this in the future if needed
        return true;
    }
}
