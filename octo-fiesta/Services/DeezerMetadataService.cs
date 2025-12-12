using octo_fiesta.Models;
using System.Text.Json;

namespace octo_fiesta.Services;

/// <summary>
/// Implémentation du service de métadonnées utilisant l'API Deezer (gratuite, pas besoin de clé)
/// </summary>
public class DeezerMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.deezer.com";

    public DeezerMetadataService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
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
                    songs.Add(ParseDeezerTrack(track));
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
        // Exécuter les recherches en parallèle
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
        if (externalProvider != "deezer") return null;
        
        var url = $"{BaseUrl}/track/{externalId}";
        var response = await _httpClient.GetAsync(url);
        
        if (!response.IsSuccessStatusCode) return null;
        
        var json = await response.Content.ReadAsStringAsync();
        var track = JsonDocument.Parse(json).RootElement;
        
        if (track.TryGetProperty("error", out _)) return null;
        
        // Pour un track individuel, on récupère les métadonnées complètes
        var song = ParseDeezerTrackFull(track);
        
        // Récupérer les infos supplémentaires depuis l'album (genre, nombre total de tracks, label, copyright)
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
                    
                    // Nombre total de tracks
                    if (albumData.TryGetProperty("nb_tracks", out var nbTracks))
                    {
                        song.TotalTracks = nbTracks.GetInt32();
                    }
                    
                    // Label
                    if (albumData.TryGetProperty("label", out var label))
                    {
                        song.Label = label.GetString();
                    }
                    
                    // Cover art XL si pas déjà définie
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
                // Si on ne peut pas récupérer l'album, on continue avec les infos du track
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
        
        // Récupérer les chansons de l'album
        if (albumElement.TryGetProperty("tracks", out var tracks) && 
            tracks.TryGetProperty("data", out var tracksData))
        {
            int trackIndex = 1;
            foreach (var track in tracksData.EnumerateArray())
            {
                // Pass the index as fallback for track_position (Deezer doesn't include it in album tracks)
                album.Songs.Add(ParseDeezerTrack(track, trackIndex));
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

    private Song ParseDeezerTrack(JsonElement track, int? fallbackTrackNumber = null)
    {
        var externalId = track.GetProperty("id").GetInt64().ToString();
        
        // Try to get track_position from API, fallback to provided index
        int? trackNumber = track.TryGetProperty("track_position", out var trackPos) 
            ? trackPos.GetInt32() 
            : fallbackTrackNumber;
        
        return new Song
        {
            Id = $"ext-deezer-song-{externalId}",
            Title = track.GetProperty("title").GetString() ?? "",
            Artist = track.TryGetProperty("artist", out var artist) 
                ? artist.GetProperty("name").GetString() ?? "" 
                : "",
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
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId
        };
    }

    /// <summary>
    /// Parse un track Deezer avec toutes les métadonnées disponibles
    /// Utilisé pour GetSongAsync qui retourne des données complètes
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
        
        // Contributors
        var contributors = new List<string>();
        if (track.TryGetProperty("contributors", out var contribs))
        {
            foreach (var contrib in contribs.EnumerateArray())
            {
                if (contrib.TryGetProperty("name", out var contribName))
                {
                    var name = contribName.GetString();
                    if (!string.IsNullOrEmpty(name))
                        contributors.Add(name);
                }
            }
        }
        
        // Album artist (premier artiste de l'album, ou artiste principal du track)
        string? albumArtist = null;
        if (track.TryGetProperty("album", out var albumForArtist) && 
            albumForArtist.TryGetProperty("artist", out var albumArtistEl))
        {
            albumArtist = albumArtistEl.TryGetProperty("name", out var aName) 
                ? aName.GetString() 
                : null;
        }
        
        // Cover art URLs (différentes tailles)
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
        
        return new Song
        {
            Id = $"ext-deezer-song-{externalId}",
            Title = track.GetProperty("title").GetString() ?? "",
            Artist = track.TryGetProperty("artist", out var artist) 
                ? artist.GetProperty("name").GetString() ?? "" 
                : "",
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
            ExternalId = externalId
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
            Genre = album.TryGetProperty("genres", out var genres) && 
                    genres.TryGetProperty("data", out var genresData) &&
                    genresData.GetArrayLength() > 0
                ? genresData[0].GetProperty("name").GetString()
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
            ImageUrl = artist.TryGetProperty("picture_medium", out var picture)
                ? picture.GetString()
                : null,
            AlbumCount = artist.TryGetProperty("nb_album", out var nbAlbum) 
                ? nbAlbum.GetInt32() 
                : null,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId
        };
    }
}
