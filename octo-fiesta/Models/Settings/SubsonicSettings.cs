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
    Qobuz,
    
    /// <summary>
    /// SquidWTF music service (supports Qobuz and Tidal backends)
    /// DEPRECATED: upstream squid.wtf music services are down; may be removed in a future release
    /// </summary>
    SquidWTF,

    /// <summary>
    /// Yandex music service
    /// </summary>
    Yandex,

    /// <summary>
    /// Tidal music service, using your own account through Tidal's official API
    /// </summary>
    Tidal,

    /// <summary>
    /// Apple Music through an alacarte instance (see AppleMusicSettings)
    /// </summary>
    AppleMusic,

    /// <summary>
    /// GD Studio aggregator API (netease, joox, bilibili, ...)
    /// </summary>
    GDStudio
}

public partial class SubsonicSettings
{
    public string? Url { get; set; }

    /// <summary>
    /// Admin username for server-to-server actions that require admin privileges.
    /// Environment variable: SUBSONIC_ADMIN_USERNAME
    /// Both admin username and password has to be set to use it.
    /// If not set the user credentials will be used to perform server-to-server actions
    /// (this may cause problems if the user has no admin permissions on the navidrome server)
    /// </summary>
    public string? AdminUsername { get; set; }

    /// <summary>
    /// Admin password for server-to-server actions that require admin privileges.
    /// Environment variable: SUBSONIC_ADMIN_PASSWORD
    /// Both admin username and password has to be set to use it.
    /// If not set the user credentials will be used to perform server-to-server actions
    /// (this may cause problems if the user has no admin permissions on the navidrome server)
    /// </summary>
    public string? AdminPassword { get; set; }
    
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
    /// Music service(s) to use (default: Deezer)
    /// Environment variable: MUSIC_SERVICE
    /// Values: "Deezer", "Qobuz", "Tidal", "Yandex", "GDStudio", "AppleMusic", "SquidWTF" (deprecated).
    /// Several can be combined with "," (e.g. "Deezer,Qobuz"): searches are merged and
    /// providers without valid credentials are skipped with a warning.
    /// </summary>
    [Microsoft.Extensions.Configuration.ConfigurationKeyName("MusicService")]
    public string MusicServices { get; set; } = MusicService.Deezer.ToString();

    /// <summary>
    /// The first (primary) entry of <see cref="MusicServices"/>. Read-only: it is derived, so
    /// a multi-value config such as "Deezer,Qobuz" never has to bind to an enum.
    /// </summary>
    // Derived, so it must not bind to the "MusicService" config key (that key holds the raw list).
    [Microsoft.Extensions.Configuration.ConfigurationKeyName("PrimaryMusicService")]
    public MusicService MusicService => ParseMusicServices(MusicServices, out _).FirstOrDefault();

    /// <summary>
    /// Parses a ","-separated list (also "|" or ";") (case-insensitive) into distinct services; unrecognised
    /// entries are reported in <paramref name="unknown"/>. Blank input means Deezer.
    /// </summary>
    public static List<MusicService> ParseMusicServices(string? value, out List<string> unknown)
    {
        unknown = [];
        var result = new List<MusicService>();
        foreach (var part in (value ?? "").Split(['|', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<MusicService>(part, ignoreCase: true, out var svc) && Enum.IsDefined(svc))
            {
                if (!result.Contains(svc)) result.Add(svc);
            }
            else unknown.Add(part);
        }
        if (result.Count == 0 && unknown.Count == 0) result.Add(MusicService.Deezer);
        return result;
    }
    
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

    /// <summary>
    /// What to do when a download would land on a file name that already exists (default: false)
    /// Environment variable: ALLOW_BITRATE_UPGRADE
    /// false: skip the download and keep the existing file.
    /// true: download only if the new track has a higher bitrate than the existing file,
    /// and replace the file once the download completes.
    /// </summary>
    public bool AllowBitrateUpgrade { get; set; } = false;
    
    /// <summary>
    /// Template for organizing downloaded files into folders (default: {artist}/{album}/{track} - {title})
    /// Environment variable: FOLDER_TEMPLATE
    /// Available placeholders: {artistLetter}, {artist}, {album}, {title}, {track}, {disc}, {year}, {genre}, {quality}
    /// Slashes (/) separate folder levels; the last segment becomes the file name.
    /// </summary>
    public string FolderTemplate { get; set; } = "{artist}/{album}/{track} - {title}";

    /// <summary>
    /// Disable triggering a Subsonic library scan after a download completes (default: false)
    /// Environment variable: DISABLE_LIBRARY_SCAN
    /// Useful when the Subsonic server picks up new files on its own or through an external
    /// automation, avoiding costly full library scans (e.g. Plex via Plexsonic).
    /// </summary>
    public bool DisableLibraryScan { get; set; } = false;
}
