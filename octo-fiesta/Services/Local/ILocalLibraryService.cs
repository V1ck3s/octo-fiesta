using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.Local;

/// <summary>
/// Interface for local music library management
/// </summary>
public interface ILocalLibraryService
{
    /// <summary>
    /// Checks if an external song already exists locally
    /// </summary>
    Task<string?> GetLocalPathForExternalSongAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Registers a downloaded song in the local library
    /// </summary>
    Task RegisterDownloadedSongAsync(Song song, string localPath, string? downloadedQuality = null);
    
    /// <summary>
    /// Gets the full mapping for an external song (includes quality info)
    /// </summary>
    Task<LocalSongMapping?> GetMappingForExternalSongAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Gets the mapping between external ID and local ID
    /// </summary>
    Task<string?> GetLocalIdForExternalSongAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Parses a song ID to determine if it is external or local
    /// </summary>
    (bool isExternal, string? provider, string? externalId) ParseSongId(string songId);
    
    /// <summary>
    /// Parses an external ID to extract the provider, type and ID
    /// Format: ext-{provider}-{type}-{id} (e.g., ext-deezer-artist-259, ext-deezer-album-96126, ext-deezer-song-12345)
    /// Also supports legacy format: ext-{provider}-{id} (assumes song type)
    /// </summary>
    (bool isExternal, string? provider, string? type, string? externalId) ParseExternalId(string id);
    
    /// <summary>
    /// Triggers a Subsonic library scan
    /// </summary>
    Task<bool> TriggerLibraryScanAsync();
    
    /// <summary>
    /// Gets the current scan status
    /// </summary>
    Task<ScanStatus?> GetScanStatusAsync();
}
