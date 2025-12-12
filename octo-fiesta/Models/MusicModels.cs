namespace octo_fiesta.Models;

/// <summary>
/// Represents a song (local or external)
/// </summary>
public class Song
{
    /// <summary>
    /// Unique ID. For external songs, prefixed with "ext-" + provider + "-" + external id
    /// Example: "ext-deezer-123456" or "local-789"
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? ArtistId { get; set; }
    public string Album { get; set; } = string.Empty;
    public string? AlbumId { get; set; }
    public int? Duration { get; set; } // In seconds
    public int? Track { get; set; }
    public int? DiscNumber { get; set; }
    public int? TotalTracks { get; set; }
    public int? Year { get; set; }
    public string? Genre { get; set; }
    public string? CoverArtUrl { get; set; }
    
    /// <summary>
    /// High-resolution cover art URL (for embedding)
    /// </summary>
    public string? CoverArtUrlLarge { get; set; }
    
    /// <summary>
    /// BPM (beats per minute) if available
    /// </summary>
    public int? Bpm { get; set; }
    
    /// <summary>
    /// ISRC (International Standard Recording Code)
    /// </summary>
    public string? Isrc { get; set; }
    
    /// <summary>
    /// Full release date (format: YYYY-MM-DD)
    /// </summary>
    public string? ReleaseDate { get; set; }
    
    /// <summary>
    /// Album artist name (may differ from track artist)
    /// </summary>
    public string? AlbumArtist { get; set; }
    
    /// <summary>
    /// Composer(s)
    /// </summary>
    public string? Composer { get; set; }
    
    /// <summary>
    /// Album label
    /// </summary>
    public string? Label { get; set; }
    
    /// <summary>
    /// Copyright
    /// </summary>
    public string? Copyright { get; set; }
    
    /// <summary>
    /// Contributing artists (features, etc.)
    /// </summary>
    public List<string> Contributors { get; set; } = new();
    
    /// <summary>
    /// Indicates whether the song is available locally or needs to be downloaded
    /// </summary>
    public bool IsLocal { get; set; }
    
    /// <summary>
    /// External provider (deezer, spotify, etc.) - null if local
    /// </summary>
    public string? ExternalProvider { get; set; }
    
    /// <summary>
    /// ID on the external provider (for downloading)
    /// </summary>
    public string? ExternalId { get; set; }
    
    /// <summary>
    /// Local file path (if available)
    /// </summary>
    public string? LocalPath { get; set; }
}

/// <summary>
/// Represents an artist
/// </summary>
public class Artist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public int? AlbumCount { get; set; }
    public bool IsLocal { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
}

/// <summary>
/// Represents an album
/// </summary>
public class Album
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? ArtistId { get; set; }
    public int? Year { get; set; }
    public int? SongCount { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? Genre { get; set; }
    public bool IsLocal { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
    public List<Song> Songs { get; set; } = new();
}

/// <summary>
/// Search result combining local and external results
/// </summary>
public class SearchResult
{
    public List<Song> Songs { get; set; } = new();
    public List<Album> Albums { get; set; } = new();
    public List<Artist> Artists { get; set; } = new();
}

/// <summary>
/// Download status of a song
/// </summary>
public enum DownloadStatus
{
    NotStarted,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// Information about an ongoing or completed download
/// </summary>
public class DownloadInfo
{
    public string SongId { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string ExternalProvider { get; set; } = string.Empty;
    public DownloadStatus Status { get; set; }
    public double Progress { get; set; } // 0.0 to 1.0
    public string? LocalPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Subsonic library scan status
/// </summary>
public class ScanStatus
{
    public bool Scanning { get; set; }
    public int? Count { get; set; }
}
