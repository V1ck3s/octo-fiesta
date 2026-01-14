using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;

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
    /// Downloads remaining tracks from an album in background (excluding the specified track)
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, spotify)</param>
    /// <param name="albumExternalId">The album ID on the external provider</param>
    /// <param name="excludeTrackExternalId">The track ID to exclude (already downloaded)</param>
    void DownloadRemainingAlbumTracksInBackground(string externalProvider, string albumExternalId, string excludeTrackExternalId);
    
    /// <summary>
    /// Checks if a song is currently being downloaded
    /// </summary>
    DownloadInfo? GetDownloadStatus(string songId);
    
    /// <summary>
    /// Gets the local path for a song if it has been downloaded already
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, qobuz, etc.)</param>
    /// <param name="externalId">The ID on the external provider</param>
    /// <returns>The local file path if exists, null otherwise</returns>
    Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Checks if the service is properly configured and functional
    /// </summary>
    Task<bool> IsAvailableAsync();
}
