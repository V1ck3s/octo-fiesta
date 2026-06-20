namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for synchronized lyrics lookup, bound from the "Lyrics" section
/// (environment variables Lyrics__*).
/// </summary>
public class LyricsSettings
{
    /// <summary>
    /// Master switch for the lyrics feature (default: true).
    /// When disabled, getLyricsBySongId for external songs returns an empty
    /// lyrics list and no .lrc sidecar files are written.
    /// Environment variable: Lyrics__Enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL of the LRCLIB-compatible instance (default: https://lrclib.net).
    /// Lets users point at a self-hosted LRCLIB mirror.
    /// Environment variable: Lyrics__LrclibBaseUrl
    /// </summary>
    public string LrclibBaseUrl { get; set; } = "https://lrclib.net";

    /// <summary>
    /// Write a .lrc sidecar file next to permanently downloaded tracks (default: true).
    /// The backing Subsonic server (e.g. Navidrome) then serves the synced lyrics for
    /// the stored copy to every client, including on later listens.
    /// Environment variable: Lyrics__WriteLrcFile
    /// </summary>
    public bool WriteLrcFile { get; set; } = true;

    /// <summary>
    /// Fall back to plain (unsynced) lyrics when no synced lyrics are found (default: true).
    /// Environment variable: Lyrics__AllowPlainFallback
    /// </summary>
    public bool AllowPlainFallback { get; set; } = true;

    /// <summary>
    /// HTTP timeout in seconds for lyric provider requests (default: 8).
    /// Environment variable: Lyrics__TimeoutSeconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 8;
}
