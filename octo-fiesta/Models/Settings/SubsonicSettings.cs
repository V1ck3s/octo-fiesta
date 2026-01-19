namespace octo_fiesta.Models.Settings;

/// <summary>
/// Download mode for tracks
/// </summary>
public enum DownloadMode
{
    /// <summary>
    /// Download only the requested track (default behavior)
    /// </summary>
    Track,
    
    /// <summary>
    /// When a track is played, download the entire album in background
    /// The requested track is downloaded first, then remaining tracks are queued
    /// </summary>
    Album
}

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

/// <summary>
/// Storage mode for downloaded tracks
/// </summary>
public enum StorageMode
{
    /// <summary>
    /// Files are permanently stored in the library and registered in the database
    /// </summary>
    Permanent,
    
    /// <summary>
    /// Files are stored in a temporary cache and automatically cleaned up
    /// Not registered in the database, no Navidrome scan triggered
    /// </summary>
    Cache
}

/// <summary>
/// Music service provider
/// </summary>
public enum MusicService
{
    /// <summary>
    /// Deezer music service
    /// </summary>
    Deezer,
    
    /// <summary>
    /// Qobuz music service
    /// </summary>
    Qobuz
}

public class SubsonicSettings
{
    public string? Url { get; set; }
    
    /// <summary>
    /// Explicit content filter mode (default: All)
    /// Environment variable: EXPLICIT_FILTER
    /// Values: "All", "ExplicitOnly", "CleanOnly"
    /// Note: Only works with Deezer
    /// </summary>
    public ExplicitFilter ExplicitFilter { get; set; } = ExplicitFilter.All;
    
    /// <summary>
    /// Download mode for tracks (default: Track)
    /// Environment variable: DOWNLOAD_MODE
    /// Values: "Track" (download only played track), "Album" (download full album when playing a track)
    /// </summary>
    public DownloadMode DownloadMode { get; set; } = DownloadMode.Track;
    
    /// <summary>
    /// Music service to use (default: Deezer)
    /// Environment variable: MUSIC_SERVICE
    /// Values: "Deezer", "Qobuz"
    /// </summary>
    public MusicService MusicService { get; set; } = MusicService.Deezer;
    
    /// <summary>
    /// Storage mode for downloaded files (default: Permanent)
    /// Environment variable: STORAGE_MODE
    /// Values: "Permanent" (files saved to library), "Cache" (temporary files, auto-cleanup)
    /// </summary>
    public StorageMode StorageMode { get; set; } = StorageMode.Permanent;
    
    /// <summary>
    /// Cache duration in hours for Cache storage mode (default: 1)
    /// Environment variable: CACHE_DURATION_HOURS
    /// Files older than this duration will be automatically deleted
    /// Only applies when StorageMode is Cache
    /// </summary>
    public int CacheDurationHours { get; set; } = 1;
    
    /// <summary>
    /// Enable external playlist search and streaming (default: true)
    /// Environment variable: ENABLE_EXTERNAL_PLAYLISTS
    /// When enabled, users can search for playlists from the configured music provider
    /// Playlists appear as "albums" in search results with genre "Playlist"
    /// </summary>
    public bool EnableExternalPlaylists { get; set; } = true;
    
    /// <summary>
    /// Directory name for storing playlist .m3u files (default: "playlists")
    /// Environment variable: PLAYLISTS_DIRECTORY
    /// Relative to the music library root directory
    /// Playlist files will be stored in {MusicDirectory}/{PlaylistsDirectory}/
    /// </summary>
    public string PlaylistsDirectory { get; set; } = "playlists";
    
    /// <summary>
    /// Automatically re-download tracks when higher quality is available (default: false)
    /// Environment variable: AUTO_UPGRADE_QUALITY
    /// When enabled, if an existing track is MP3 and FLAC quality is now available,
    /// the track will be re-downloaded in FLAC
    /// </summary>
    public bool AutoUpgradeQuality { get; set; } = false;
}