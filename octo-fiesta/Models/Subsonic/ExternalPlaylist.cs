namespace octo_fiesta.Models.Subsonic;

/// <summary>
/// Represents a playlist from an external music provider (Deezer, Qobuz).
/// </summary>
public class ExternalPlaylist
{
    /// <summary>
    /// Unique identifier in the format "pl-{provider}-{externalId}"
    /// Example: "pl-deezer-123456" or "pl-qobuz-789"
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>
    /// Playlist name
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Playlist description
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// Name of the playlist creator/curator
    /// </summary>
    public string? CuratorName { get; set; }
    
    /// <summary>
    /// Provider name ("deezer" or "qobuz")
    /// </summary>
    public string Provider { get; set; } = string.Empty;
    
    /// <summary>
    /// External ID from the provider (without "pl-" prefix)
    /// </summary>
    public string ExternalId { get; set; } = string.Empty;
    
    /// <summary>
    /// Number of tracks in the playlist
    /// </summary>
    public int TrackCount { get; set; }
    
    /// <summary>
    /// Total duration in seconds
    /// </summary>
    public int Duration { get; set; }
    
    /// <summary>
    /// Cover art URL from the provider
    /// </summary>
    public string? CoverUrl { get; set; }
    
    /// <summary>
    /// Playlist creation date
    /// </summary>
    public DateTime? CreatedDate { get; set; }
}
