namespace octo_fiesta.Models.Settings;

public class JioSaavnSettings
{
    /// <summary>
    /// Preferred audio quality. Available: 96, 160, 320. Defaults to 320.
    /// </summary>
    public string Quality { get; set; } = "320";

    /// <summary>
    /// Base URL of a self-hosted instance of the JioSaavn API (see deploy/jiosaavn-api).
    /// Third-party public instances of this API have repeatedly gone offline without notice
    /// (personal Vercel/Workers deployments with no uptime guarantee), so this points at a
    /// self-hosted instance by default. No trailing slash.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://jiosaavn-api:3000";
}
