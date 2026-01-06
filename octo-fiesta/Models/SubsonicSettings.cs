namespace octo_fiesta.Models;

/// <summary>
/// Explicit content filter mode for Deezer tracks
/// </summary>
public enum ExplicitFilter
{
    /// <summary>
    /// Show all tracks (no filtering)
    /// </summary>
    All,
    
    /// <summary>
    /// Exclude clean/edited versions (explicit_content_lyrics == 3)
    /// Shows original explicit content and naturally clean content
    /// </summary>
    ExplicitOnly,
    
    /// <summary>
    /// Only show clean content (explicit_content_lyrics == 0 or 3)
    /// Excludes tracks with explicit_content_lyrics == 1
    /// </summary>
    CleanOnly
}

public class SubsonicSettings
{
    public string? Url { get; set; }
    
    /// <summary>
    /// Explicit content filter mode (default: All)
    /// Environment variable: EXPLICIT_FILTER
    /// Values: "All", "ExplicitOnly", "CleanOnly"
    /// </summary>
    public ExplicitFilter ExplicitFilter { get; set; } = ExplicitFilter.All;
}