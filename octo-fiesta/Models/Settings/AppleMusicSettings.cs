namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the Apple Music provider, which is backed by an alacarte
/// instance (https://github.com/sosjalapeno/alacarte). alacarte searches the
/// Apple Music catalog and downloads into the same library octo-fiesta serves.
/// </summary>
public class AppleMusicSettings
{
    /// <summary>
    /// Base URL of the alacarte web UI, e.g. http://192.168.1.10:7373
    /// </summary>
    public string? AlacarteUrl { get; set; }

    /// <summary>
    /// alacarte integration API token (Settings -> octo-fiesta Integration in alacarte)
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// How long to wait for alacarte to finish downloading one song.
    /// Default: 15 minutes
    /// </summary>
    public int DownloadTimeoutSeconds { get; set; } = 900;
}
