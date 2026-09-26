using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.AppleMusic;

/// <summary>
/// Maps ALACarte's Apple Music catalog records onto octo-fiesta's domain models.
/// </summary>
public static class AppleMusicMapper
{
    public const string Provider = "applemusic";
    public const string AlbumPrefix = "ext-applemusic-album-";
    public const string ArtistPrefix = "ext-applemusic-artist-";

    public static Song ToSong(AlacarteSong s, AlacarteAlbum? album = null)
    {
        var artistId = s.ArtistId != null ? ArtistPrefix + s.ArtistId : null;
        return new Song
        {
            Title = s.Title,
            Artist = s.Artist,
            ArtistId = artistId,
            Artists = string.IsNullOrEmpty(s.Artist)
                ? new List<Artist>()
                : new List<Artist>
                {
                    new() { Id = artistId ?? "", Name = s.Artist, IsLocal = false, ExternalProvider = Provider, ExternalId = s.ArtistId },
                },
            Album = s.Album,
            AlbumId = s.AlbumId != null ? AlbumPrefix + s.AlbumId : null,
            AlbumArtist = s.AlbumArtist ?? album?.Artist ?? s.Artist,
            Duration = s.DurationMs is { } ms ? (int)Math.Round(ms / 1000.0) : null,
            Track = s.Track,
            DiscNumber = s.Disc,
            TotalTracks = album?.TrackCount,
            Year = s.Year ?? album?.Year,
            Genre = s.Genre ?? album?.Genre,
            CoverArtUrl = s.ArtworkUrl ?? album?.ArtworkUrl,
            CoverArtUrlLarge = s.ArtworkUrlLarge ?? album?.ArtworkUrlLarge,
            Isrc = s.Isrc,
            ReleaseDate = s.ReleaseDate,
            Composer = s.Composer,
            Label = album?.Label,
            Copyright = album?.Copyright,
            ExplicitContentLyrics = s.Explicit ? 1 : 0,
            ReleaseType = album?.ReleaseType,
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = s.Id,
        };
    }

    public static Album ToAlbum(AlacarteAlbum a) => new()
    {
        Id = AlbumPrefix + a.Id,
        Title = a.Title,
        Artist = a.Artist,
        ArtistId = a.ArtistId != null ? ArtistPrefix + a.ArtistId : null,
        Year = a.Year,
        SongCount = a.TrackCount ?? a.Tracks?.Count,
        ReleaseType = a.ReleaseType,
        CoverArtUrl = a.ArtworkUrl,
        CoverArtUrlLarge = a.ArtworkUrlLarge,
        Genre = a.Genre,
        IsLocal = false,
        ExternalProvider = Provider,
        ExternalId = a.Id,
        Songs = a.Tracks?.Select(t => ToSong(t, a)).ToList() ?? new List<Song>(),
    };

    public static Artist ToArtist(AlacarteArtist a) => new()
    {
        Id = ArtistPrefix + a.Id,
        Name = a.Name,
        ImageUrl = a.ArtworkUrl,
        AlbumCount = a.Albums?.Count,
        IsLocal = false,
        ExternalProvider = Provider,
        ExternalId = a.Id,
    };

    public static ExternalPlaylist ToPlaylist(AlacartePlaylist p) => new()
    {
        Id = PlaylistIdHelper.CreatePlaylistId(Provider, p.Id),
        Name = p.Name,
        Description = p.Description,
        CuratorName = p.Curator,
        Provider = Provider,
        ExternalId = p.Id,
        TrackCount = p.TrackCount ?? p.Tracks?.Count ?? 0,
        Duration = (int)((p.Tracks?.Sum(t => t.DurationMs ?? 0) ?? 0) / 1000),
        CoverUrl = p.ArtworkUrl,
    };

    /// <summary>
    /// Playlist tracks are presented as an album named after the playlist,
    /// like the other providers do.
    /// </summary>
    public static List<Song> ToPlaylistSongs(AlacartePlaylist p) =>
        (p.Tracks ?? new List<AlacarteSong>())
            .Select((t, i) =>
            {
                var song = ToSong(t);
                song.Track = i + 1;
                song.Album = p.Name;
                song.AlbumId = PlaylistIdHelper.CreatePlaylistId(Provider, p.Id);
                return song;
            })
            .ToList();
}

/// <summary>
/// Apple Music metadata through ALACarte's integration API.
/// </summary>
public class AppleMusicMetadataService : IMusicMetadataService
{
    private readonly AlacarteClient _client;

    public AppleMusicMetadataService(AlacarteClient client)
    {
        _client = client;
    }

    private static bool IsOurs(string externalProvider) => externalProvider == AppleMusicMapper.Provider;

    // Songs ALACarte already has in the library come back from Navidrome as
    // local results, so they are left out here instead of showing twice.
    private static List<Song> NewSongs(IEnumerable<AlacarteSong>? songs) =>
        songs?.Where(s => !s.InLibrary).Select(s => AppleMusicMapper.ToSong(s)).ToList() ?? new();

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20) =>
        NewSongs((await _client.SearchAsync(query, limit, 0, 0, 0))?.Songs);

    private static List<Album> NewAlbums(IEnumerable<AlacarteAlbum>? albums) =>
        albums?.Where(a => !a.InLibrary).Select(AppleMusicMapper.ToAlbum).ToList() ?? new();

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20) =>
        NewAlbums((await _client.SearchAsync(query, 0, limit, 0, 0))?.Albums);

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20) =>
        (await _client.SearchAsync(query, 0, 0, limit, 0))?.Artists.Select(AppleMusicMapper.ToArtist).ToList() ?? new();

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var result = await _client.SearchAsync(query, songLimit, albumLimit, artistLimit, 0);
        return new SearchResult
        {
            Songs = NewSongs(result?.Songs),
            Albums = NewAlbums(result?.Albums),
            Artists = result?.Artists.Select(AppleMusicMapper.ToArtist).ToList() ?? new(),
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (!IsOurs(externalProvider)) return null;
        var song = await _client.GetSongAsync(externalId);
        return song == null ? null : AppleMusicMapper.ToSong(song);
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (!IsOurs(externalProvider)) return null;
        var album = await _client.GetAlbumAsync(externalId);
        return album == null ? null : AppleMusicMapper.ToAlbum(album);
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (!IsOurs(externalProvider)) return null;
        var artist = await _client.GetArtistAsync(externalId);
        return artist == null ? null : AppleMusicMapper.ToArtist(artist);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (!IsOurs(externalProvider)) return new();
        var artist = await _client.GetArtistAsync(externalId);
        return artist?.Albums?.Select(AppleMusicMapper.ToAlbum).ToList() ?? new();
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) =>
        (await _client.SearchAsync(query, 0, 0, 0, limit))?.Playlists.Select(AppleMusicMapper.ToPlaylist).ToList() ?? new();

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (!IsOurs(externalProvider)) return null;
        var playlist = await _client.GetPlaylistAsync(externalId);
        return playlist == null ? null : AppleMusicMapper.ToPlaylist(playlist);
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        if (!IsOurs(externalProvider)) return new();
        var playlist = await _client.GetPlaylistAsync(externalId);
        return playlist == null ? new() : AppleMusicMapper.ToPlaylistSongs(playlist);
    }
}
