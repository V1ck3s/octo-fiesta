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
    /// For Qobuz: 27 (FLAC 24-bit/192kHz), 7 (FLAC 24-bit/96kHz), 6 (FLAC 16-bit), 5 (MP3 320kbps)
    /// For Tidal: HI_RES_LOSSLESS (FLAC 24-bit), LOSSLESS (FLAC 16-bit), HIGH (320kbps AAC), LOW (96kbps AAC)
    /// If not specified, highest quality will be used
    /// </summary>
    public string? Quality { get; set; }
}
