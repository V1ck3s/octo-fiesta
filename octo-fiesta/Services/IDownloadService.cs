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
    /// Downloads a song from an external provider, forcing permanent storage even in Cache mode.
    /// Used when starring playlists in Cache mode.
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, qobuz, etc.)</param>
    /// <param name="externalId">The ID on the external provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The path to the downloaded file</returns>
    Task<string> DownloadSongToPermanentAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
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
    /// Downloads all tracks from an album in background.
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, spotify)</param>
    /// <param name="albumExternalId">The album ID on the external provider</param>
    void DownloadFullAlbumInBackground(string externalProvider, string albumExternalId);

    /// <summary>
    /// Downloads all tracks from an album in background, forcing permanent storage even in Cache mode.
    /// Used when starring an album in Cache mode.
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, qobuz, etc.)</param>
    /// <param name="albumExternalId">The album ID on the external provider</param>
    void DownloadFullAlbumInBackgroundToPermanent(string externalProvider, string albumExternalId);
    
    /// <summary>
    /// Checks if a song is currently being downloaded
    /// </summary>
    DownloadInfo? GetDownloadStatus(string songId);
    
    /// <summary>
    /// Gets the local path for a song if it has been downloaded already
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, qobuz, etc.)</param>
    /// <param name="externalId">The ID on the external provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The local file path if exists, null otherwise</returns>
    Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a cached song to permanent storage. Used when starring a song in Cache mode.
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, qobuz, etc.)</param>
    /// <param name="externalId">The external track ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the song was moved to permanent storage, false if not found in cache</returns>
    Task<bool> PermanentizeCachedSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks if the service is properly configured and functional
    /// </summary>
    Task<bool> IsAvailableAsync();
}
