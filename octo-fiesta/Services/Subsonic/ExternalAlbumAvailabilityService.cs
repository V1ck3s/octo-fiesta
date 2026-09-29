using System.Collections.Concurrent;

namespace octo_fiesta.Services.Subsonic;

public interface IExternalAlbumAvailabilityService
{
    void MarkDownloadStarted(string provider, string albumExternalId);
    bool IsDownloadStarted(string provider, string albumExternalId);
}

public sealed class ExternalAlbumAvailabilityService : IExternalAlbumAvailabilityService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _downloadStartedAlbums = new(StringComparer.OrdinalIgnoreCase);

    public void MarkDownloadStarted(string provider, string albumExternalId)
    {
        _downloadStartedAlbums[CreateKey(provider, albumExternalId)] = DateTimeOffset.UtcNow;
    }

    public bool IsDownloadStarted(string provider, string albumExternalId)
    {
        return _downloadStartedAlbums.ContainsKey(CreateKey(provider, albumExternalId));
    }

    private static string CreateKey(string provider, string albumExternalId)
    {
        return $"{provider}:{albumExternalId}";
    }
}
