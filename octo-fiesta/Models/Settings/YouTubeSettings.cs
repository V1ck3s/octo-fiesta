namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the YouTube provider.
/// </summary>
public class YouTubeSettings
{
    /// <summary>
    /// Enables the YouTube provider.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to the yt-dlp binary.
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Maximum number of search results returned by the provider.
    /// </summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// Preferred audio format (e.g. m4a, mp3).
    /// </summary>
    public string AudioFormat { get; set; } = "m4a";

    /// <summary>
    /// Optional path to cookies file for authenticated requests.
    /// </summary>
    public string? CookiesPath { get; set; }
}
