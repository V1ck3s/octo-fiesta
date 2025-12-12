using octo_fiesta.Models;

namespace octo_fiesta.Services;

/// <summary>
/// Interface for the music download service (Deezspot or other)
/// </summary>
public interface IDownloadService
{
    /// <summary>
    /// Downloads a song from an external provider
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, spotify)</param>
    /// <param name="externalId">The ID on the external provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The path to the downloaded file</returns>
    Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Downloads a song and streams the result progressively
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, spotify)</param>
    /// <param name="externalId">The ID on the external provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A stream of the audio file</returns>
    Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks if a song is currently being downloaded
    /// </summary>
    DownloadInfo? GetDownloadStatus(string songId);
    
    /// <summary>
    /// Checks if the service is properly configured and functional
    /// </summary>
    Task<bool> IsAvailableAsync();
}
