using octo_fiesta.Models.Download;

namespace octo_fiesta.Services.Composite;

/// <summary>
/// Routes every download call to the provider named by <c>externalProvider</c>.
/// </summary>
public class CompositeDownloadService : IDownloadService
{
    public IReadOnlyList<(string Key, IDownloadService Service)> Providers { get; }

    public CompositeDownloadService(IReadOnlyList<(string Key, IDownloadService Service)> providers)
        => Providers = providers;

    private IDownloadService For(string provider)
    {
        var match = Providers.FirstOrDefault(p => p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Service;
        return match ?? throw new NotSupportedException($"Provider '{provider}' is not enabled");
    }

    public Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
        => For(externalProvider).DownloadSongAsync(externalProvider, externalId, cancellationToken);

    public Task<string> DownloadSongToPermanentAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
        => For(externalProvider).DownloadSongToPermanentAsync(externalProvider, externalId, cancellationToken);

    public Task<(Stream Stream, string FilePath)> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
        => For(externalProvider).DownloadAndStreamAsync(externalProvider, externalId, cancellationToken);

    public void UpgradeQualityInBackground(string externalProvider, string externalId)
        => For(externalProvider).UpgradeQualityInBackground(externalProvider, externalId);

    public void DownloadRemainingAlbumTracksInBackground(string externalProvider, string albumExternalId, string excludeTrackExternalId)
        => For(externalProvider).DownloadRemainingAlbumTracksInBackground(externalProvider, albumExternalId, excludeTrackExternalId);

    public void DownloadFullAlbumInBackground(string externalProvider, string albumExternalId)
        => For(externalProvider).DownloadFullAlbumInBackground(externalProvider, albumExternalId);

    public void DownloadFullAlbumInBackgroundToPermanent(string externalProvider, string albumExternalId)
        => For(externalProvider).DownloadFullAlbumInBackgroundToPermanent(externalProvider, albumExternalId);

    public DownloadInfo? GetDownloadStatus(string songId)
        => Providers.Select(p => p.Service.GetDownloadStatus(songId)).FirstOrDefault(i => i != null);

    public Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
        => For(externalProvider).GetLocalPathIfExistsAsync(externalProvider, externalId, cancellationToken);

    public Task<bool> PermanentizeCachedSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
        => For(externalProvider).PermanentizeCachedSongAsync(externalProvider, externalId, cancellationToken);

    public async Task<bool> IsAvailableAsync()
    {
        foreach (var p in Providers)
            if (await p.Service.IsAvailableAsync()) return true;
        return false;
    }
}
