using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Models.Tidal;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.Tidal;

/// <summary>
/// Metadata service backed by Tidal's own API, authenticated with the user's account.
/// </summary>
public class TidalMetadataService : IMusicMetadataService
{
    public const string ProviderName = "tidal";

    /// <summary>
    /// Tidal caps collection endpoints at 100 items per page.
    /// </summary>
    private const int MaxPageSize = 100;

    private readonly HttpClient _httpClient;
    private readonly TidalAuthService _auth;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<TidalMetadataService> _logger;

    public TidalMetadataService(
        IHttpClientFactory httpClientFactory,
        TidalAuthService auth,
        IOptions<SubsonicSettings> subsonicSettings,
        ILogger<TidalMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient(TidalHttpClientConfiguration.AuthClientName);
        _auth = auth;
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;
    }

    #region IMusicMetadataService Implementation

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        var result = await SearchAsync(query, "TRACKS", limit);
        if (result?.Tracks?.Items is null) return [];

        return result.Tracks.Items
            .Select(MapTrackToSong)
            .Where(ShouldIncludeSong)
            .ToList();
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        var result = await SearchAsync(query, "ALBUMS", limit);
        if (result?.Albums?.Items is null) return [];

        return result.Albums.Items.Select(MapAlbum).ToList();
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        var result = await SearchAsync(query, "ARTISTS", limit);
        if (result?.Artists?.Items is null) return [];

        return result.Artists.Items.Select(MapArtist).ToList();
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        // One call covers the three types: /search accepts a comma-separated type list.
        var limit = Math.Max(songLimit, Math.Max(albumLimit, artistLimit));
        var result = await SearchAsync(query, "TRACKS,ALBUMS,ARTISTS", limit);

        if (result is null)
        {
            return new SearchResult();
        }

        return new SearchResult
        {
            Songs = result.Tracks?.Items?
                .Select(MapTrackToSong)
                .Where(ShouldIncludeSong)
                .Take(songLimit)
                .ToList() ?? [],
            Albums = result.Albums?.Items?.Take(albumLimit).Select(MapAlbum).ToList() ?? [],
            Artists = result.Artists?.Items?.Take(artistLimit).Select(MapArtist).ToList() ?? []
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;

        var json = await SendAsync($"/tracks/{externalId}");
        if (json is null) return null;

        var track = Deserialize<TidalTrack>(json);
        return track is null ? null : MapTrackToSong(track);
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;

        var albumJson = await SendAsync($"/albums/{externalId}");
        if (albumJson is null) return null;

        var albumData = Deserialize<TidalAlbum>(albumJson);
        if (albumData is null) return null;

        var album = MapAlbum(albumData);

        var itemsJson = await SendAsync($"/albums/{externalId}/items?limit={MaxPageSize}");
        var items = itemsJson is null ? null : Deserialize<TidalItemsResponse<TidalItemWrapper>>(itemsJson);

        foreach (var track in EnumerateTracks(items))
        {
            var song = MapTrackToSong(track);

            // The track payload nested in a collection carries a trimmed-down album, so
            // fill the gaps from the album we just fetched.
            song.Album = album.Title;
            song.AlbumId = album.Id;
            song.AlbumArtist = album.Artist;
            song.Year ??= album.Year;
            song.Genre ??= album.Genre;
            song.TotalTracks ??= album.SongCount;
            song.ReleaseType ??= album.ReleaseType;
            song.CoverArtUrl ??= album.CoverArtUrl;
            song.CoverArtUrlLarge ??= album.CoverArtUrlLarge;

            if (ShouldIncludeSong(song))
            {
                album.Songs.Add(song);
            }
        }

        return album;
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;

        var json = await SendAsync($"/artists/{externalId}");
        if (json is null) return null;

        var artist = Deserialize<TidalArtist>(json);
        return artist is null ? null : MapArtist(artist);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return [];

        // Albums and EPs/singles are two separate filters on the same endpoint.
        var albumsJson = await SendAsync($"/artists/{externalId}/albums?limit={MaxPageSize}");
        var singlesJson = await SendAsync($"/artists/{externalId}/albums?filter=EPSANDSINGLES&limit={MaxPageSize}");

        var albums = new List<TidalAlbum>();
        foreach (var json in new[] { albumsJson, singlesJson })
        {
            if (json is null) continue;
            var page = Deserialize<TidalItemsResponse<TidalAlbum>>(json);
            if (page?.Items is not null)
            {
                albums.AddRange(page.Items);
            }
        }

        return albums
            .DistinctBy(a => a.Id)
            .Select(MapAlbum)
            .ToList();
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        var result = await SearchAsync(query, "PLAYLISTS", limit);
        if (result?.Playlists?.Items is null) return [];

        return result.Playlists.Items.Select(MapPlaylist).ToList();
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;

        var json = await SendAsync($"/playlists/{externalId}");
        if (json is null) return null;

        var playlist = Deserialize<TidalPlaylist>(json);
        return playlist is null ? null : MapPlaylist(playlist);
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return [];

        var songs = new List<Song>();
        var offset = 0;

        while (true)
        {
            var json = await SendAsync($"/playlists/{externalId}/items?limit={MaxPageSize}&offset={offset}");
            if (json is null) break;

            var page = Deserialize<TidalItemsResponse<TidalItemWrapper>>(json);
            var tracks = EnumerateTracks(page).ToList();
            if (tracks.Count == 0) break;

            foreach (var track in tracks)
            {
                var song = MapTrackToSong(track);
                if (!ShouldIncludeSong(song)) continue;

                // Playlist order, not the track's position on its own album.
                song.Track = songs.Count + 1;
                songs.Add(song);
            }

            offset += page?.Items?.Count ?? tracks.Count;
            if (page?.Items is null || page.Items.Count < MaxPageSize || offset >= page.TotalNumberOfItems)
            {
                break;
            }
        }

        return songs;
    }

    #endregion

    #region API access

    private async Task<TidalSearchResponse?> SearchAsync(string query, string types, int limit)
    {
        var url = $"/search?query={Uri.EscapeDataString(query)}"
                  + $"&limit={Math.Clamp(limit, 1, MaxPageSize)}&offset=0&types={types}";

        var json = await SendAsync(url);
        return json is null ? null : Deserialize<TidalSearchResponse>(json);
    }

    /// <summary>
    /// Sends an authenticated GET against api.tidal.com. The country code is appended
    /// automatically because every catalogue endpoint requires it.
    /// </summary>
    private async Task<string?> SendAsync(string path)
    {
        try
        {
            var separator = path.Contains('?') ? '&' : '?';
            var countryCode = await _auth.GetCountryCodeAsync();
            var url = $"{TidalHttpClientConfiguration.ApiBaseUrl}{path}{separator}countryCode={countryCode}";

            using var request = await _auth.CreateAuthenticatedRequestAsync(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tidal API returned {StatusCode} for {Path}", response.StatusCode, path);
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (InvalidOperationException ex)
        {
            // Missing or revoked credentials. Every search would otherwise dump a stack trace.
            _logger.LogWarning("Skipping the Tidal API call for {Path}: {Reason}", path, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call the Tidal API for {Path}", path);
            return null;
        }
    }

    private T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse a Tidal {Type} response", typeof(T).Name);
            return null;
        }
    }

    /// <summary>
    /// Album and playlist collections also contain videos, which have no downloadable audio.
    /// </summary>
    private static IEnumerable<TidalTrack> EnumerateTracks(TidalItemsResponse<TidalItemWrapper>? page)
        => page?.Items?
               .Where(i => string.Equals(i.Type, "track", StringComparison.OrdinalIgnoreCase) && i.Item is not null)
               .Select(i => i.Item!)
           ?? [];

    #endregion

    #region Mapping

    private static Song MapTrackToSong(TidalTrack track)
    {
        var artists = track.Artists?
            .Where(a => !string.IsNullOrEmpty(a.Name))
            .Select(MapArtist)
            .ToList() ?? [];

        // Ensure the main artist is present when the artists array is empty.
        if (artists.Count == 0 && !string.IsNullOrEmpty(track.Artist?.Name))
        {
            artists.Add(MapArtist(track.Artist));
        }

        var title = track.Title ?? "";
        if (!string.IsNullOrEmpty(track.Version))
        {
            title += $" ({track.Version})";
        }

        return new Song
        {
            Title = title,
            Artist = track.Artist?.Name ?? artists.FirstOrDefault()?.Name ?? "",
            Artists = artists,
            ArtistId = track.Artist is not null
                ? BuildId("artist", track.Artist.Id)
                : artists.FirstOrDefault()?.Id,
            Album = track.Album?.Title ?? "",
            AlbumId = track.Album is not null ? BuildId("album", track.Album.Id) : null,
            AlbumArtist = track.Album?.Artist?.Name,
            Duration = track.Duration,
            Track = track.TrackNumber,
            DiscNumber = track.VolumeNumber,
            TotalTracks = track.Album?.NumberOfTracks,
            Year = ParseYear(track.Album?.ReleaseDate),
            ReleaseDate = track.Album?.ReleaseDate,
            ReleaseType = track.Album?.Type,
            Isrc = track.Isrc,
            Bpm = track.Bpm,
            Copyright = track.Copyright,
            CoverArtUrl = BuildCoverUrl(track.Album?.Cover),
            CoverArtUrlLarge = BuildCoverUrl(track.Album?.Cover, "1280x1280"),
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = track.Id.ToString(),
            ExplicitContentLyrics = track.Explicit ? 1 : 0
        };
    }

    private static Album MapAlbum(TidalAlbum album)
    {
        var mainArtist = album.Artist ?? album.Artists?.FirstOrDefault();

        return new Album
        {
            Id = BuildId("album", album.Id),
            Title = album.Title ?? "",
            Artist = mainArtist?.Name ?? "",
            ArtistId = mainArtist is not null ? BuildId("artist", mainArtist.Id) : null,
            Year = ParseYear(album.ReleaseDate),
            SongCount = album.NumberOfTracks,
            ReleaseType = album.Type,
            CoverArtUrl = BuildCoverUrl(album.Cover),
            CoverArtUrlLarge = BuildCoverUrl(album.Cover, "1280x1280"),
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = album.Id.ToString()
        };
    }

    private static Artist MapArtist(TidalArtist artist) => new()
    {
        Id = BuildId("artist", artist.Id),
        Name = artist.Name ?? "",
        ImageUrl = BuildImageUrl(artist.Picture),
        IsLocal = false,
        ExternalProvider = ProviderName,
        ExternalId = artist.Id.ToString()
    };

    private static ExternalPlaylist MapPlaylist(TidalPlaylist playlist) => new()
    {
        Id = PlaylistIdHelper.CreatePlaylistId(ProviderName, playlist.Uuid ?? ""),
        Name = playlist.Title ?? "",
        Description = playlist.Description,
        CuratorName = playlist.Creator?.Name,
        Provider = ProviderName,
        ExternalId = playlist.Uuid ?? "",
        TrackCount = playlist.NumberOfTracks,
        Duration = playlist.Duration,
        CoverUrl = BuildCoverUrl(playlist.SquareImage ?? playlist.Image),
        CreatedDate = ParseCreatedDate(playlist.Created)
    };

    private static DateTime? ParseCreatedDate(string? created)
        => DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.UtcDateTime
            : null;

    private static string BuildId(string type, long id) => $"ext-{ProviderName}-{type}-{id}";

    private static int? ParseYear(string? releaseDate)
    {
        if (releaseDate is null || releaseDate.Length < 4) return null;
        return int.TryParse(releaseDate[..4], out var year) ? year : null;
    }

    /// <summary>
    /// Cover and picture ids are UUIDs whose dashes become path separators on the CDN.
    /// </summary>
    private static string? BuildCoverUrl(string? coverId, string size = "320x320")
    {
        if (string.IsNullOrEmpty(coverId)) return null;
        return $"https://resources.tidal.com/images/{coverId.Replace("-", "/")}/{size}.jpg";
    }

    private static string? BuildImageUrl(string? pictureId) => BuildCoverUrl(pictureId, "750x750");

    #endregion

    /// <summary>
    /// Applies the explicit content filter. Tidal only exposes an explicit flag, so there
    /// is no clean/edited variant to distinguish.
    /// </summary>
    private bool ShouldIncludeSong(Song song) => _subsonicSettings.ExplicitFilter switch
    {
        ExplicitFilter.CleanOnly => song.ExplicitContentLyrics != 1,
        _ => true
    };
}
