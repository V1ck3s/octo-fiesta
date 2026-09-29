using octo_fiesta.Models.Domain;

namespace octo_fiesta.Services.Local;

public partial interface ILocalLibraryService
{
    Task RegisterDownloadedSongAsync(
        Song song,
        string localPath,
        string? downloadedQuality,
        string? localSubsonicId);

    Task<IReadOnlyDictionary<string, LocalSongMapping>> GetMappingsSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<LocalSongMatch?> FindLocalSongByMetadataAsync(Song song);
}
