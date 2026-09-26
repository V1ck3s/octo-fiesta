namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the Apple Music provider, which is backed by an ALACarte
/// instance (https://github.com/sosjalapeno/alacarte). ALACarte searches the
/// Apple Music catalog and downloads into the same library octo-fiesta serves.
/// </summary>
public class AppleMusicSettings
{
    /// <summary>
    /// Base URL of the ALACarte web UI, e.g. http://192.168.1.10:7373
    /// </summary>
    public string? AlacarteUrl { get; set; }

    /// <summary>
    /// ALACarte integration API token (ALACARTE_API_TOKEN on the ALACarte side)
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// How long to wait for ALACarte to finish downloading one song.
    /// Default: 15 minutes
    /// </summary>
    public int DownloadTimeoutSeconds { get; set; } = 900;
}
