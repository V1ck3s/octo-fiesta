namespace octo_fiesta.Models;

/// <summary>
/// Configuration for the Qobuz downloader and metadata service
/// </summary>
public class QobuzSettings
{
    /// <summary>
    /// Qobuz user authentication token
    /// Obtained from browser's localStorage after logging into play.qobuz.com
    /// </summary>
    public string? UserAuthToken { get; set; }
    
    /// <summary>
    /// Qobuz user ID
    /// Obtained from browser's localStorage after logging into play.qobuz.com
    /// </summary>
    public string? UserId { get; set; }
    
    /// <summary>
    /// Preferred audio quality: FLAC, MP3_320, MP3_128
    /// If not specified or unavailable, the highest available quality will be used.
    /// </summary>
    public string? Quality { get; set; }
}
