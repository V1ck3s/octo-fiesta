using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.AppleMusic;

/// <summary>
/// Serves Apple Music next to the configured primary provider: searches both
/// (primary results first) and routes every lookup by the provider in the id.
/// </summary>
public class MultiProviderMetadataService : IMusicMetadataService
{
    private readonly IMusicMetadataService _primary;
    private readonly IMusicMetadataService _appleMusic;

    public MultiProviderMetadataService(IMusicMetadataService primary, IMusicMetadataService appleMusic)
    {
        _primary = primary;
        _appleMusic = appleMusic;
    }

    private IMusicMetadataService For(string externalProvider) =>
        externalProvider == AppleMusicMapper.Provider ? _appleMusic : _primary;

    private static async Task<List<T>> BothAsync<T>(Task<List<T>> primary, Task<List<T>> appleMusic)
    {
        await Task.WhenAll(primary, appleMusic);
        return (await primary).Concat(await appleMusic).ToList();
    }

    public Task<List<Song>> SearchSongsAsync(string query, int limit = 20) =>
        BothAsync(_primary.SearchSongsAsync(query, limit), _appleMusic.SearchSongsAsync(query, limit));

    public Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20) =>
        BothAsync(_primary.SearchAlbumsAsync(query, limit), _appleMusic.SearchAlbumsAsync(query, limit));

    public Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20) =>
        BothAsync(_primary.SearchArtistsAsync(query, limit), _appleMusic.SearchArtistsAsync(query, limit));

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var primary = _primary.SearchAllAsync(query, songLimit, albumLimit, artistLimit);
        var appleMusic = _appleMusic.SearchAllAsync(query, songLimit, albumLimit, artistLimit);
        await Task.WhenAll(primary, appleMusic);
        var p = await primary;
        var a = await appleMusic;
        return new SearchResult
        {
            Songs = p.Songs.Concat(a.Songs).ToList(),
            Albums = p.Albums.Concat(a.Albums).ToList(),
            Artists = p.Artists.Concat(a.Artists).ToList(),
        };
    }

    public Task<Song?> GetSongAsync(string externalProvider, string externalId) =>
        For(externalProvider).GetSongAsync(externalProvider, externalId);

    public Task<Album?> GetAlbumAsync(string externalProvider, string externalId) =>
        For(externalProvider).GetAlbumAsync(externalProvider, externalId);

    public Task<string?> GetAlbumCoverUrlAsync(string externalProvider, string externalId) =>
        For(externalProvider).GetAlbumCoverUrlAsync(externalProvider, externalId);

    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId) =>
        For(externalProvider).GetArtistAsync(externalProvider, externalId);

    public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId) =>
        For(externalProvider).GetArtistAlbumsAsync(externalProvider, externalId);

    public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) =>
        BothAsync(_primary.SearchPlaylistsAsync(query, limit), _appleMusic.SearchPlaylistsAsync(query, limit));

    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId) =>
        For(externalProvider).GetPlaylistAsync(externalProvider, externalId);

    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId) =>
        For(externalProvider).GetPlaylistTracksAsync(externalProvider, externalId);
}

/// <summary>
/// Routes downloads to Apple Music or the primary provider by the provider in the id.
/// </summary>
public class MultiProviderDownloadService : IDownloadService
{
    private readonly IDownloadService _primary;
    private readonly IDownloadService _appleMusic;

    public MultiProviderDownloadService(IDownloadService primary, IDownloadService appleMusic)
    {
        _primary = primary;
        _appleMusic = appleMusic;
    }

    private IDownloadService For(string externalProvider) =>
        externalProvider == AppleMusicMapper.Provider ? _appleMusic : _primary;

    public Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        For(externalProvider).DownloadSongAsync(externalProvider, externalId, cancellationToken);

    public Task<string> DownloadSongToPermanentAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        For(externalProvider).DownloadSongToPermanentAsync(externalProvider, externalId, cancellationToken);

    public Task<(Stream Stream, string FilePath)> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        For(externalProvider).DownloadAndStreamAsync(externalProvider, externalId, cancellationToken);

    public void UpgradeQualityInBackground(string externalProvider, string externalId) =>
        For(externalProvider).UpgradeQualityInBackground(externalProvider, externalId);

    public void DownloadRemainingAlbumTracksInBackground(string externalProvider, string albumExternalId, string excludeTrackExternalId) =>
        For(externalProvider).DownloadRemainingAlbumTracksInBackground(externalProvider, albumExternalId, excludeTrackExternalId);

    public void DownloadFullAlbumInBackground(string externalProvider, string albumExternalId) =>
        For(externalProvider).DownloadFullAlbumInBackground(externalProvider, albumExternalId);

    public void DownloadFullAlbumInBackgroundToPermanent(string externalProvider, string albumExternalId) =>
        For(externalProvider).DownloadFullAlbumInBackgroundToPermanent(externalProvider, albumExternalId);

    public DownloadInfo? GetDownloadStatus(string songId) =>
        _appleMusic.GetDownloadStatus(songId) ?? _primary.GetDownloadStatus(songId);

    public Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        For(externalProvider).GetLocalPathIfExistsAsync(externalProvider, externalId, cancellationToken);

    public Task<bool> PermanentizeCachedSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
        For(externalProvider).PermanentizeCachedSongAsync(externalProvider, externalId, cancellationToken);

    public Task<bool> IsAvailableAsync() => _primary.IsAvailableAsync();
}
