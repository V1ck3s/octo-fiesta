namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the SquidWTF music provider
/// SquidWTF is a music downloader service that supports Qobuz and Tidal backends
/// </summary>
public class SquidWTFSettings
{
    /// <summary>
    /// The backend source to use: "Qobuz" or "Tidal"
    /// Defaults to "Qobuz" if not specified
    /// </summary>
    public string Source { get; set; } = "Qobuz";
    
    /// <summary>
    /// Preferred audio quality
    /// For Qobuz: 27 (FLAC), 7 (MP3 320), 5 (MP3 128)
    /// For Tidal: "HI_RES_LOSSLESS", "LOSSLESS"
    /// If not specified, highest quality will be used
    /// </summary>
    public string? Quality { get; set; }
}
