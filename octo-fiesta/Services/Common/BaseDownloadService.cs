using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Lyrics;
using octo_fiesta.Services.Subsonic;
using TagLib;
using IOFile = System.IO.File;
using System.Collections.Concurrent;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Abstract base class for download services.
/// Implements common download logic, tracking, and metadata writing.
/// Subclasses implement provider-specific download and authentication logic.
/// </summary>
public abstract class BaseDownloadService : IDownloadService, IDisposable
{
    protected readonly IConfiguration Configuration;
    protected readonly ILocalLibraryService LocalLibraryService;
    protected readonly IMusicMetadataService MetadataService;
    protected readonly SubsonicSettings SubsonicSettings;
    protected readonly ILogger Logger;
    private readonly IServiceProvider _serviceProvider;

    // A property, not a constructor field, so ProviderName is never read while a derived
    // instance is still being built.
    protected string DownloadPath => DownloadPathResolver.GetProviderPath(Configuration, ProviderName);

    protected readonly string CachePath;

    protected readonly ConcurrentDictionary<string, DownloadInfo> ActiveDownloads = new();

    // The download running for each track, which every other request for that track joins.
    // Key: "ext-{provider}-{externalId}"
    private readonly ConcurrentDictionary<string, TrackDownload> _runningDownloads = new();

    // Every download takes one of MaxConcurrentDownloads slots. With StreamWhileDownloading,
    // playback requests skip this queue, so a background album never holds up the track
    // being listened to.
    private readonly SemaphoreSlim _backgroundQueue;

    // Scratch files for progressive downloads. Kept out of the download folder so library
    // watchers never see half-written tracks.
    private static readonly string StreamingPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-stream");
    private static readonly TimeSpan StaleStreamingFileAge = TimeSpan.FromHours(6);

    // A finished download that was streamed keeps serving its raw file for the track's length
    // plus this margin. The tagged file has a different layout, so a client that started on the raw
    // bytes would land elsewhere when seeking in it.
    private static readonly TimeSpan FinishedStreamMargin = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, ProgressiveDownload> _finishedStreams = new();
    private volatile bool _disposed;

    // Enough bytes to recognise the audio format before the content type is sent
    private const int FormatHeaderBytes = 12;

    // Small in-memory cache to avoid repeated metadata/network lookups when searching for cached files.
    // Key: "{provider}|{externalId}" -> (path or null, expiry)
    private readonly ConcurrentDictionary<string, (string? Path, DateTime Expiry)> _metadataPathCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataPathLocks = new();

    // Tracks queued for a quality upgrade, and those the provider cannot serve any higher
    private readonly ConcurrentDictionary<string, byte> _pendingQualityUpgrades = new();
    private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MetadataCacheNegativeTtl = TimeSpan.FromMinutes(1);
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly TimeSpan MetadataCacheCleanupInterval = TimeSpan.FromMinutes(5);
    private readonly object _metadataCacheCleanupLock = new();
    private DateTime _metadataCacheNextCleanupUtc = DateTime.UtcNow.Add(MetadataCacheCleanupInterval);

    /// <summary>
    /// Lazy-loaded PlaylistSyncService to avoid circular dependency
    /// </summary>
    private PlaylistSyncService? _playlistSyncService;
    protected PlaylistSyncService? PlaylistSyncService
    {
        get
        {
            if (_playlistSyncService == null)
            {
                _playlistSyncService = _serviceProvider.GetService<PlaylistSyncService>();
            }
            return _playlistSyncService;
        }
    }

    /// <summary>
    /// Lazy-loaded lyrics service (optional). Used to drop a .lrc sidecar next to
    /// permanently downloaded tracks so the backing server serves synced lyrics.
    /// </summary>
    private ILyricsService? _lyricsService;
    protected ILyricsService? LyricsService
        => _lyricsService ??= _serviceProvider.GetService<ILyricsService>();

    /// <summary>
    /// Provider name (e.g., "deezer", "qobuz")
    /// </summary>
    protected abstract string ProviderName { get; }

    protected BaseDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        SubsonicSettings subsonicSettings,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        Configuration = configuration;
        LocalLibraryService = localLibraryService;
        MetadataService = metadataService;
        SubsonicSettings = subsonicSettings;
        _serviceProvider = serviceProvider;
        Logger = logger;

        var maxConcurrentDownloads = Math.Clamp(SubsonicSettings.MaxConcurrentDownloads, 1, SubsonicSettings.MaxConcurrentDownloadsLimit);
        _backgroundQueue = new(maxConcurrentDownloads, maxConcurrentDownloads);

        CachePath = PathHelper.GetCachePath();

        if (!Directory.Exists(CachePath))
        {
            Directory.CreateDirectory(CachePath);
        }

        DeleteStaleStreamingFiles();
    }

    // Scratch files left behind by a crash. Age-based, since several provider services share the folder.
    private void DeleteStaleStreamingFiles()
    {
        try
        {
            if (!Directory.Exists(StreamingPath))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(StreamingPath, "*.part"))
            {
                if (DateTime.UtcNow - IOFile.GetLastWriteTimeUtc(file) > StaleStreamingFileAge)
                {
                    TryDeleteIncompleteFile(file);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to clean up streaming scratch files");
        }
    }

    #region IDownloadService Implementation

    public async Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, forcePermanent: false, cancellationToken);
    }

    public async Task<string> DownloadSongToPermanentAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, forcePermanent: true, cancellationToken);
    }

    /// <summary>
    /// Serves the finished track. With StreamWhileDownloading, returns a stream as soon as the
    /// first bytes of the track are on disk when the provider supports it; tagging, library
    /// registration and album queueing then finish in the background.
    /// </summary>
    public async Task<(Stream Stream, string FilePath)> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        EnsureProvider(externalProvider);

        string localPath;
        if (!SubsonicSettings.StreamWhileDownloading)
        {
            localPath = await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, cancellationToken: cancellationToken);
        }
        else
        {
            var songId = $"ext-{ProviderName}-{externalId}";
            if (_finishedStreams.TryGetValue(songId, out var finished) && finished.TryOpenReader() is { } finishedReader)
            {
                return (finishedReader, finished.TargetPath);
            }

            // Playback skips the background queue, and follows the track's download if one is already running
            var download = await StartOrJoinAsync(new DownloadRequest(externalId, TriggerAlbumDownload: true, ForcePermanent: false), cancellationToken);

            if (await download.Readable.WaitAsync(cancellationToken) is { } progressive)
            {
                // A head start, so the player does not run dry while the download gets up to speed
                await progressive.WaitForSecondsAsync(SubsonicSettings.StreamPrebufferSeconds, cancellationToken);
                if (progressive.TryOpenReader() is { } reader)
                {
                    Logger.LogInformation("Streaming {SongId} while it downloads", download.SongId);
                    return (reader, progressive.TargetPath);
                }
            }

            localPath = await download.Completed.WaitAsync(cancellationToken);
        }

        // FileShare.Delete allows move/rename operations while the file is being streamed (required for cache-to-permanent on star)
        var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return (stream, localPath);
    }

    public DownloadInfo? GetDownloadStatus(string songId)
    {
        ActiveDownloads.TryGetValue(songId, out var info);
        return info;
    }

    public async Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != ProviderName)
        {
            return null;
        }

        // Check local library
        var localPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (localPath != null && IOFile.Exists(localPath))
        {
            return localPath;
        }

        // Check cache directory
        var cachedPath = await GetCachedFilePathAsync(externalProvider, externalId, cancellationToken);
        if (cachedPath != null && IOFile.Exists(cachedPath))
        {
            return cachedPath;
        }

        return null;
    }

    public abstract Task<bool> IsAvailableAsync();

    public void DownloadRemainingAlbumTracksInBackground(string externalProvider, string albumExternalId, string excludeTrackExternalId)
    {
        if (externalProvider != ProviderName)
        {
            Logger.LogWarning("Provider '{Provider}' is not supported for album download", externalProvider);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadRemainingAlbumTracksAsync(albumExternalId, excludeTrackExternalId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to download remaining album tracks for album {AlbumId}", albumExternalId);
            }
        });
    }

    public void UpgradeQualityInBackground(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName)
        {
            return;
        }

        _ = Task.Run(() => UpgradeQualityAsync(externalId, triggerAlbumDownload: true));
    }

    private async Task UpgradeQualityAsync(string externalId, bool triggerAlbumDownload, CancellationToken cancellationToken = default)
    {
        var key = $"{ProviderName}:{externalId}";
        if (!_pendingQualityUpgrades.TryAdd(key, 0))
        {
            return;
        }

        // The provider may simply not offer the target quality. In that case the key is
        // left in place so later plays stop re-downloading the same track for nothing.
        var upgradeUnavailable = false;
        try
        {
            var mapping = await LocalLibraryService.GetMappingForExternalSongAsync(ProviderName, externalId);
            if (mapping == null || !IsQualityUpgradeAvailable(mapping.DownloadedQuality))
            {
                return;
            }

            await DownloadSongInternalAsync(ProviderName, externalId, triggerAlbumDownload, cancellationToken: cancellationToken);

            var upgraded = await LocalLibraryService.GetMappingForExternalSongAsync(ProviderName, externalId);
            upgradeUnavailable = upgraded != null && IsQualityUpgradeAvailable(upgraded.DownloadedQuality);
            if (upgradeUnavailable)
            {
                Logger.LogInformation("{Provider}:{ExternalId} is not available above {Quality}, giving up on the upgrade",
                    ProviderName, externalId, upgraded!.DownloadedQuality ?? "unknown");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Quality upgrade failed for {Provider}:{ExternalId}", ProviderName, externalId);
        }
        finally
        {
            if (!upgradeUnavailable)
            {
                _pendingQualityUpgrades.TryRemove(key, out _);
            }
        }
    }

    public void DownloadFullAlbumInBackground(string externalProvider, string albumExternalId)
    {
        DownloadFullAlbumInBackgroundInternal(externalProvider, albumExternalId, forcePermanent: false);
    }

    /// <summary>
    /// Downloads all tracks from an album in background, forcing permanent storage even in Cache mode.
    /// Used when starring an album in Cache mode.
    /// </summary>
    public void DownloadFullAlbumInBackgroundToPermanent(string externalProvider, string albumExternalId)
    {
        DownloadFullAlbumInBackgroundInternal(externalProvider, albumExternalId, forcePermanent: true);
    }

    private void DownloadFullAlbumInBackgroundInternal(string externalProvider, string albumExternalId, bool forcePermanent)
    {
        if (externalProvider != ProviderName)
        {
            Logger.LogWarning("Provider '{Provider}' is not supported for album download", externalProvider);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadFullAlbumAsync(albumExternalId, forcePermanent);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to download full album {AlbumId}", albumExternalId);
            }
        });
    }

    /// <summary>
    /// Moves a cached song to permanent storage. Used when starring a song in Cache mode.
    /// If the song is not in cache, returns false (caller should handle this case).
    /// </summary>
    /// <param name="externalProvider">The provider (deezer, qobuz, etc.)</param>
    /// <param name="externalId">The external track ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the song was moved to permanent storage, false if not found in cache</returns>
    public async Task<bool> PermanentizeCachedSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != ProviderName)
        {
            return false;
        }

        var cachedPath = await GetCachedFilePathAsync(externalProvider, externalId, cancellationToken);
        if (cachedPath == null || !IOFile.Exists(cachedPath))
        {
            Logger.LogDebug("Song not found in cache for permanentization: {Provider}:{ExternalId}", externalProvider, externalId);
            return false;
        }

        Logger.LogInformation("Permanentizing cached song: {Provider}:{ExternalId} from {CachedPath}", externalProvider, externalId, cachedPath);

        // Capture old Navidrome ID and affected playlists BEFORE the move
        string? oldNavidromeId = null;
        List<(string PlaylistId, string PlaylistName)>? affectedPlaylists = null;
        try
        {
            oldNavidromeId = await LocalLibraryService.GetLocalIdForExternalSongAsync(externalProvider, externalId);
            if (!string.IsNullOrEmpty(oldNavidromeId))
            {
                affectedPlaylists = await LocalLibraryService.FindPlaylistsContainingSongAsync(oldNavidromeId);
                if (affectedPlaylists.Count > 0)
                {
                    Logger.LogInformation(
                        "Song {Provider}:{ExternalId} (Navidrome ID {OldId}) found in {Count} playlist(s) - will migrate after scan",
                        externalProvider, externalId, oldNavidromeId, affectedPlaylists.Count);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to capture playlist context before permanentization for {Provider}:{ExternalId}",
                externalProvider, externalId);
        }

        // Get song metadata (with correct AlbumArtist)
        Song? song = null;
        var tempSong = await MetadataService.GetSongAsync(externalProvider, externalId);
        if (tempSong != null && !string.IsNullOrEmpty(tempSong.AlbumId) && !PlaylistIdHelper.IsExternalPlaylist(tempSong.AlbumId))
        {
            var albumExternalId = ExtractExternalIdFromAlbumId(tempSong.AlbumId);
            if (!string.IsNullOrEmpty(albumExternalId))
            {
                var album = await MetadataService.GetAlbumAsync(externalProvider, albumExternalId);
                if (album != null)
                {
                    song = album.Songs.FirstOrDefault(s => s.ExternalId == externalId);
                    if (song == null && !string.IsNullOrEmpty(album.Artist))
                    {
                        tempSong.AlbumArtist = album.Artist;
                    }
                }
            }
        }
        song ??= tempSong;

        if (song == null)
        {
            Logger.LogWarning("Could not retrieve metadata for permanentization: {Provider}:{ExternalId}", externalProvider, externalId);
            return false;
        }

        // Determine quality from file extension (best effort since we don't have it stored)
        var extension = Path.GetExtension(cachedPath);
        var downloadedQuality = extension.ToLowerInvariant() switch
        {
            ".flac" => "FLAC",
            ".mp3" => "MP3",
            _ => null
        };

        // Build permanent path
        var permanentPath = PathHelper.BuildTrackPath(
            DownloadPath,
            song,
            extension,
            SubsonicSettings.FolderTemplate,
            downloadedQuality);

        // Create directory structure
        var permanentDir = Path.GetDirectoryName(permanentPath);
        if (!string.IsNullOrEmpty(permanentDir) && !Directory.Exists(permanentDir))
        {
            Directory.CreateDirectory(permanentDir);
        }

        // Move file (atomic on same filesystem, copy+delete on cross-filesystem)
        try
        {
            IOFile.Move(cachedPath, permanentPath);
            Logger.LogInformation("Moved cached song to permanent storage: {PermanentPath}", permanentPath);
        }
        catch (IOException) when (IOFile.Exists(permanentPath))
        {
            // File already exists at destination (maybe from a previous download)
            Logger.LogInformation("File already exists at permanent path, removing cached copy: {PermanentPath}", permanentPath);
            IOFile.Delete(cachedPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to move cached song to permanent storage: {CachedPath} -> {PermanentPath}", cachedPath, permanentPath);
            return false;
        }

        // Invalidate the metadata path cache
        var cacheKey = $"{externalProvider}|{externalId}";
        _metadataPathCache.TryRemove(cacheKey, out _);

        // Register in mappings
        song.LocalPath = permanentPath;
        await LocalLibraryService.RegisterDownloadedSongAsync(song, permanentPath, downloadedQuality);

        // Drop a .lrc sidecar next to the now-permanent file (best-effort, fire-and-forget).
        if (LyricsService is { Enabled: true })
        {
            var sidecarService = LyricsService;
            var sidecarPath = permanentPath;
            var sidecarSong = song;
            _ = Task.Run(() => sidecarService.TryWriteSidecarAsync(sidecarPath, sidecarSong, CancellationToken.None));
        }

        // Trigger library scan and migrate playlists in background
        var capturedOldId = oldNavidromeId;
        var capturedPlaylists = affectedPlaylists;
        var capturedProvider = externalProvider;
        var capturedExternalId = externalId;

        _ = Task.Run(async () =>
        {
            try
            {
                var newNavidromeId = await LocalLibraryService.WaitForLocalIdAfterScanAsync(capturedProvider, capturedExternalId);

                if (!string.IsNullOrEmpty(capturedOldId) &&
                    !string.IsNullOrEmpty(newNavidromeId) &&
                    capturedOldId != newNavidromeId &&
                    capturedPlaylists is { Count: > 0 })
                {
                    Logger.LogInformation(
                        "Navidrome ID changed from {OldId} to {NewId} for {Provider}:{ExternalId} - migrating {Count} playlist(s)",
                        capturedOldId, newNavidromeId, capturedProvider, capturedExternalId, capturedPlaylists.Count);

                    await LocalLibraryService.MigratePlaylistEntriesAsync(capturedOldId, newNavidromeId, capturedPlaylists);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to complete post-permanentization tasks for {Provider}:{ExternalId}",
                    capturedProvider, capturedExternalId);
            }
        });

        return true;
    }

    #endregion

    #region Template Methods (to be implemented by subclasses)

    /// <summary>
    /// Result of a track download containing Stream with track content, preferred filename extension and quality.
    /// <paramref name="Mp4DurationSeconds"/>, when set for an MP4/M4A file, is written into the moov
    /// duration fields after download — fragmented MP4 (Tidal HI_RES FLAC-in-MP4) otherwise reports 0:00.
    /// </summary>
    public record DownloadResult(Stream DownloadStream, string Extension, string? DownloadedQuality, double? Mp4DurationSeconds = null)
    {
        /// <summary>
        /// Exact size of <see cref="DownloadStream"/> when the provider announces it. Set only when
        /// the stream yields exactly that many bytes; it lets playback start before the download ends.
        /// </summary>
        public long? ContentLength { get; init; }
    }

    /// <summary>
    /// Downloads a track and saves it to disk.
    /// Subclasses implement provider-specific logic (encryption, authentication, etc.)
    /// </summary>
    /// <param name="trackId">External track ID</param>
    /// <param name="song">Song metadata</param>
    /// <param name="forcePermanent">If true, downloads to permanent storage even in Cache mode</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Download result with local file path and quality</returns>
    protected abstract Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken);

    /// <summary>
    /// Extracts the external album ID from the internal album ID format.
    /// Example: "ext-deezer-album-123456" -> "123456"
    /// </summary>
    protected abstract string? ExtractExternalIdFromAlbumId(string albumId);

    /// <summary>
    /// Gets the target quality setting for this provider.
    /// Used for quality upgrade comparison.
    /// </summary>
    protected abstract string? GetTargetQuality();

    private bool IsQualityUpgradeAvailable(string? downloadedQuality)
    {
        return SubsonicSettings.AutoUpgradeQuality
            && QualityHelper.ShouldUpgrade(downloadedQuality, GetTargetQuality());
    }

    #endregion

    #region Common Download Logic

    /// <summary>
    /// What to download and where it goes.
    /// </summary>
    /// <param name="ExternalId">The external track ID</param>
    /// <param name="TriggerAlbumDownload">Whether to trigger album download in Album mode</param>
    /// <param name="ForcePermanent">If true, downloads to permanent storage even in Cache mode (used for star album/playlist)</param>
    /// <param name="Metadata">The track as already fetched with its album, which spares the provider the metadata lookups</param>
    private sealed record DownloadRequest(string ExternalId, bool TriggerAlbumDownload, bool ForcePermanent, Song? Metadata = null);

    /// <summary>
    /// A track download in progress. Every request for the same track shares it.
    /// </summary>
    private sealed class TrackDownload(string songId, bool toCache)
    {
        private readonly TaskCompletionSource<ProgressiveDownload?> _readable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SongId { get; } = songId;

        public bool ToCache { get; } = toCache;

        /// <summary>
        /// The download as soon as its first bytes can be read, or null, at the latest just before
        /// <see cref="Completed"/>, when the track is only served once finished.
        /// </summary>
        public Task<ProgressiveDownload?> Readable => _readable.Task;

        /// <summary>The finished track's path.</summary>
        public Task<string> Completed => _completed.Task;

        public void SetReadable(ProgressiveDownload download) => _readable.TrySetResult(download);

        public void Complete(string path)
        {
            _readable.TrySetResult(null);
            _completed.TrySetResult(path);
        }

        public void Fail(Exception error)
        {
            _readable.TrySetResult(null);
            _completed.TrySetException(error);
            // Logged by the download itself; a reader that started early may never look at it
            _ = _completed.Task.Exception;
        }
    }

    private void EnsureProvider(string externalProvider)
    {
        if (externalProvider != ProviderName)
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }
    }

    /// <summary>
    /// Downloads a track in one of the MaxConcurrentDownloads slots. Joining a download already
    /// running for the track does not take a slot.
    /// </summary>
    private async Task<string> DownloadSongInternalAsync(string externalProvider, string externalId, bool triggerAlbumDownload, bool forcePermanent = false, CancellationToken cancellationToken = default, Song? metadata = null)
    {
        EnsureProvider(externalProvider);

        if (_runningDownloads.TryGetValue($"ext-{ProviderName}-{externalId}", out var running)
            && running.ToCache == IsCacheDownload(forcePermanent))
        {
            return await running.Completed.WaitAsync(cancellationToken);
        }

        await _backgroundQueue.WaitAsync(cancellationToken);
        try
        {
            var download = await StartOrJoinAsync(new DownloadRequest(externalId, triggerAlbumDownload, forcePermanent, metadata), cancellationToken);
            return await download.Completed.WaitAsync(cancellationToken);
        }
        finally
        {
            _backgroundQueue.Release();
        }
    }

    /// <summary>
    /// Returns the download running for the track, or starts one. A request that only joins
    /// keeps its own cancellation token for waiting; the download runs with the starter's.
    /// </summary>
    private async Task<TrackDownload> StartOrJoinAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        var songId = $"ext-{ProviderName}-{request.ExternalId}";
        var toCache = IsCacheDownload(request.ForcePermanent);

        while (true)
        {
            var candidate = new TrackDownload(songId, toCache);
            var running = _runningDownloads.GetOrAdd(songId, candidate);
            if (running == candidate)
            {
                _ = RunAsync(candidate, request, cancellationToken);
                return candidate;
            }
            if (running.ToCache == toCache)
            {
                return running;
            }

            // Same track for the other storage (e.g. starred while it plays from the cache):
            // once that download ends, this one finds its file or downloads its own copy
            try
            {
                await running.Completed.WaitAsync(cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Its failure belongs to its own requests
            }
        }
    }

    // In Cache mode, downloads go to cache unless forcePermanent is set (used by star album/playlist)
    private bool IsCacheDownload(bool forcePermanent) =>
        SubsonicSettings.StorageMode == StorageMode.Cache && !forcePermanent;

    private async Task RunAsync(TrackDownload download, DownloadRequest request, CancellationToken cancellationToken)
    {
        string? path = null;
        Exception? error = null;
        try
        {
            path = await DownloadSongCoreAsync(download, request, cancellationToken);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (download.Readable.IsCompleted && download.Readable.Result is { } progressive)
        {
            if (error == null && progressive.WasStreamed)
            {
                KeepFinishedStream(download.SongId, progressive);
            }
            else
            {
                TryDeleteStreamingFile(progressive.PartPath);
            }
        }

        // Dropped before completing, so a request after a failure retries instead of joining it
        _runningDownloads.TryRemove(new KeyValuePair<string, TrackDownload>(download.SongId, download));
        if (error != null)
        {
            download.Fail(error);
        }
        else
        {
            download.Complete(path!);
        }
    }

    private async Task<string> DownloadSongCoreAsync(TrackDownload download, DownloadRequest request, CancellationToken cancellationToken)
    {
        var externalProvider = ProviderName;
        var externalId = request.ExternalId;
        var songId = download.SongId;
        var isCache = download.ToCache;
        var triggerAlbumDownload = request.TriggerAlbumDownload;

        // Tell other concurrent tasks we are started downloading routine
        // and keep reference to it for easy access
        DownloadInfo ourDownloadInfo = ActiveDownloads[songId] = new()
        {
            SongId = songId,
            ExternalId = externalId,
            ExternalProvider = externalProvider,
            Status = DownloadStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };
        
        try
        {
            // Check if already downloaded (skip for cache mode as we want to check cache folder)
            if (!isCache)
            {
                var existingMapping = await LocalLibraryService.GetMappingForExternalSongAsync(externalProvider, externalId);
                if (existingMapping != null && IOFile.Exists(existingMapping.LocalPath))
                {
                    // Check if we should upgrade quality
                    var targetQuality = GetTargetQuality();
                    bool shouldUpgrade = IsQualityUpgradeAvailable(existingMapping.DownloadedQuality);

                    // No upgrade needed – return already downloaded path
                    if (!shouldUpgrade)
                    {
                        Logger.LogInformation("Song already downloaded: {Path}", existingMapping.LocalPath);
                        ourDownloadInfo.Status = DownloadStatus.Completed;
                        ourDownloadInfo.CompletedAt = DateTime.UtcNow;
                        ourDownloadInfo.LocalPath = existingMapping.LocalPath;
                        return existingMapping.LocalPath;
                    }

                    // Back up existing track for quality upgrade
                    Logger.LogInformation("Upgrading quality from {OldQuality} to {NewQuality} for: {Path}",
                        existingMapping.DownloadedQuality ?? "unknown", targetQuality, existingMapping.LocalPath);
                    var backupPath = existingMapping.LocalPath + ".backup";
                    try
                    {
                        IOFile.Move(existingMapping.LocalPath, backupPath);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to create backup for quality upgrade, skipping upgrade");
                        ourDownloadInfo.Status = DownloadStatus.Completed;
                        ourDownloadInfo.CompletedAt = DateTime.UtcNow;
                        ourDownloadInfo.LocalPath = existingMapping.LocalPath;
                        return existingMapping.LocalPath;
                    }

                    // Store backup path to restore on failure
                    ourDownloadInfo.BackupPath = backupPath;
                }
            }
            else
            {
                // For cache mode, check if file exists in cache directory
                var cachedPath = await GetCachedFilePathAsync(externalProvider, externalId, cancellationToken);
                if (cachedPath != null && IOFile.Exists(cachedPath))
                {
                    Logger.LogInformation("Song found in cache: {Path}", cachedPath);
                    // Update file access time for cache cleanup logic
                    IOFile.SetLastAccessTime(cachedPath, DateTime.UtcNow);
                    ourDownloadInfo.Status = DownloadStatus.Completed;
                    ourDownloadInfo.CompletedAt = DateTime.UtcNow;
                    ourDownloadInfo.LocalPath = cachedPath;
                    return cachedPath;
                }
            }

            // Not tracked by our own download mapping, but the recording may already exist
            // in the Subsonic library (a different release, or moved there by an external
            // library manager). Reuse that file instead of downloading a duplicate.
            // Skipped in cache mode and during a quality upgrade (which re-downloads on purpose).
            if (!isCache && ourDownloadInfo.BackupPath == null)
            {
                var ownedPath = await LocalLibraryService.GetOwnedLibraryPathAsync(externalProvider, externalId);
                if (!string.IsNullOrEmpty(ownedPath) && IOFile.Exists(ownedPath))
                {
                    Logger.LogInformation("Song already in local library, skipping download: {Path}", ownedPath);
                    ourDownloadInfo.Status = DownloadStatus.Completed;
                    ourDownloadInfo.CompletedAt = DateTime.UtcNow;
                    ourDownloadInfo.LocalPath = ownedPath;
                    return ownedPath;
                }
            }

            Song song = request.Metadata ?? await GetSongMetadataForTrackAsync(externalProvider, externalId);
            var downloadResult = await DownloadTrackAsync(externalId, song, cancellationToken);
            string localPath;
            await using (downloadResult.DownloadStream)
            {
                localPath = await SaveDownloadStreamToFileAsync(downloadResult, song, isCache, cancellationToken,
                    SubsonicSettings.StreamWhileDownloading ? download.SetReadable : null);
            }
            song.LocalPath = localPath;

            ourDownloadInfo.Status = DownloadStatus.Completed;
            ourDownloadInfo.LocalPath = localPath;
            ourDownloadInfo.CompletedAt = DateTime.UtcNow;

            // Invalidate the metadata path cache so subsequent requests find the newly downloaded file
            var cacheKey = $"{externalProvider}|{externalId}";
            _metadataPathCache.TryRemove(cacheKey, out _);

            // Check if this track belongs to a playlist and update M3U
            if (PlaylistSyncService != null)
            {
                try
                {
                    var playlistId = PlaylistSyncService.GetPlaylistIdForTrack(songId);
                    if (playlistId != null)
                    {
                        Logger.LogInformation("Track {SongId} belongs to playlist {PlaylistId}, adding to M3U", songId, playlistId);
                        await PlaylistSyncService.AddTrackToM3UAsync(playlistId, song, localPath, isFullPlaylistDownload: false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to update playlist M3U for track {SongId}", songId);
                }
            }

            // Only register and scan if NOT in cache mode
            if (!isCache)
            {
                await LocalLibraryService.RegisterDownloadedSongAsync(song, localPath, downloadResult.DownloadedQuality);

                // Trigger a Subsonic library rescan (with debounce)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LocalLibraryService.TriggerLibraryScanAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to trigger library scan after download");
                    }
                });
            }
            else
            {
                Logger.LogInformation("Cache mode: skipping library registration and scan");
            }

            // If download mode is Album and triggering is enabled, start background download of remaining tracks
            if (triggerAlbumDownload && SubsonicSettings.DownloadMode == DownloadMode.Album && !string.IsNullOrEmpty(song.AlbumId))
            {
                var albumExternalId = ExtractExternalIdFromAlbumId(song.AlbumId);
                if (!string.IsNullOrEmpty(albumExternalId))
                {
                    Logger.LogInformation("Download mode is Album, triggering background download for album {AlbumId}", albumExternalId);
                    DownloadRemainingAlbumTracksInBackground(externalProvider, albumExternalId, externalId);
                }
            }

            Logger.LogInformation("Download completed: {Path}", localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            ourDownloadInfo.Status = DownloadStatus.Failed;
            ourDownloadInfo.ErrorMessage = ex.Message;

            // Restore backup if quality upgrade failed
            if (!string.IsNullOrEmpty(ourDownloadInfo.BackupPath) && IOFile.Exists(ourDownloadInfo.BackupPath))
            {
                try
                {
                    var originalPath = ourDownloadInfo.BackupPath.Replace(".backup", "");
                    IOFile.Move(ourDownloadInfo.BackupPath, originalPath);
                    Logger.LogInformation("Restored backup after failed quality upgrade: {Path}", originalPath);
                }
                catch (Exception restoreEx)
                {
                    Logger.LogError(restoreEx, "Failed to restore backup file: {BackupPath}", ourDownloadInfo.BackupPath);
                }
            }
            if (ex is OperationCanceledException)
            {
                Logger.LogInformation("Download canceled for {SongId}: {Message}", songId, ex.Message);
            }
            else
            {
                Logger.LogError(ex, "Download failed for {SongId}", songId);
            }
            throw;
        }
        finally
        {
            // Clean up backup file on success
            if (ourDownloadInfo.Status == DownloadStatus.Completed &&
                !string.IsNullOrEmpty(ourDownloadInfo.BackupPath) &&
                IOFile.Exists(ourDownloadInfo.BackupPath))
            {
                try
                {
                    IOFile.Delete(ourDownloadInfo.BackupPath);
                    Logger.LogInformation("Deleted backup after successful quality upgrade");
                }
                catch (Exception deleteEx)
                {
                    Logger.LogWarning(deleteEx, "Failed to delete backup file: {BackupPath}", ourDownloadInfo.BackupPath);
                }
            }
        }
    }

    /// <summary>
    /// Gets Song metadata for specified track.
    /// Used to create download file path with respect to storage template
    /// and to write metadata to song file.
    /// </summary>
    protected async Task<Song> GetSongMetadataForTrackAsync(string externalProvider, string externalId)
    {
        // Always fetch the full album to ensure AlbumArtist is correctly set.
        // Without this, tracks with featured artists get filed under the track artist instead of the album artist
        // Exception: playlists are represented as albums but should use track artist.
        Song? song = null;

        var tempSong = await MetadataService.GetSongAsync(externalProvider, externalId);
        if (tempSong != null && !string.IsNullOrEmpty(tempSong.AlbumId)
            && !PlaylistIdHelper.IsExternalPlaylist(tempSong.AlbumId))
        {
            var albumExternalId = ExtractExternalIdFromAlbumId(tempSong.AlbumId);
            if (!string.IsNullOrEmpty(albumExternalId))
            {
                // Get full album with correct AlbumArtist
                var album = await MetadataService.GetAlbumAsync(externalProvider, albumExternalId);
                if (album != null)
                {
                    // Find the track in the album to get full metadata including AlbumArtist
                    song = album.Songs.FirstOrDefault(s => s.ExternalId == externalId);

                    // If track not found in album (e.g., bonus track), use tempSong with album artist
                    if (song == null && !string.IsNullOrEmpty(album.Artist))
                    {
                        tempSong.AlbumArtist = album.Artist;
                    }
                }
            }
        }

        // Use individual song if album fetch didn't find it
        if (song == null)
        {
            song = tempSong ?? await MetadataService.GetSongAsync(externalProvider, externalId);
        }

        if (song == null)
        {
            throw new Exception("Song not found");
        }

        return song;
    }

    /// <summary>
    /// Takes DownloadResult provided by specific provider and saves it to file
    /// with respect to storage template and storage mode.
    /// Writes Song metadata to created file.
    /// </summary>
    /// <param name="result">DownloadResult containing download Stream and quality string.</param>
    /// <param name="song">Song metadata to interpolate into storage template.</param>
    /// <returns></returns>
    /// <param name="onReadable">When set and the provider announced the size, receives the download as soon as it can be streamed.</param>
    protected async Task<string> SaveDownloadStreamToFileAsync(DownloadResult result, Song song, bool toCache, CancellationToken cancellationToken, Action<ProgressiveDownload>? onReadable = null)
    {
        var basePath = toCache ? CachePath : DownloadPath;
        var outputPath = PathHelper.BuildTrackPath(basePath, song, result.Extension, SubsonicSettings.FolderTemplate, result.DownloadedQuality);

        // Create directories
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);

        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);

        // Only a file this download created is cleaned up on failure
        var created = false;
        try
        {
            // Fragmented MP4 is patched after download, so it is only ever served finished
            if (onReadable != null && result.ContentLength is > 0 && result.Mp4DurationSeconds == null)
            {
                // The scratch file is the caller's to delete once its readers are done
                var progressive = await DownloadProgressivelyAsync(result, song, outputPath, onReadable, cancellationToken);

                // Readers keep following the raw scratch file; tagging rewrites the start of
                // a file, so it works on a copy of its own.
                await using var part = new FileStream(progressive.PartPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920, useAsync: true);
                FileStream outputFile;
                while (true)
                {
                    try
                    {
                        outputFile = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, bufferSize: 81920, useAsync: true);
                        break;
                    }
                    catch (IOException) when (IOFile.Exists(outputPath))
                    {
                        // Another track took this path while this one was downloading
                        outputPath = PathHelper.ResolveUniquePath(outputPath);
                    }
                }
                created = true;
                await using (outputFile)
                {
                    await part.CopyToAsync(outputFile, cancellationToken);
                }
            }
            else
            {
                // Download the file with progress logging and stall detection
                await using var outputFile = IOFile.Create(outputPath);
                created = true;
                await CopyWithProgressAsync(result.DownloadStream, outputFile, song.Title, cancellationToken);
            }

            // Detect actual audio format from magic bytes and rename if the extension is wrong.
            // This catches cases where the stream is raw FLAC but we assumed MP4 container.
            outputPath = CorrectExtensionIfNeeded(outputPath);

            Logger.LogInformation("Downloaded file to: {Path}", outputPath);

            // Write metadata
            await WriteMetadataAsync(outputPath, song, cancellationToken);

            // Fragmented MP4 (Tidal HI_RES FLAC-in-MP4) carries no top-level duration; patch it
            // so tag scanners don't report 0:00. Done last so it survives the metadata write.
            PatchMp4DurationIfNeeded(outputPath, result);

            // For permanent files, drop a .lrc sidecar so the backing server serves synced
            // lyrics on later listens and to other clients. Fire-and-forget: never delay or
            // fail the download (the audio is already on disk).
            if (!toCache && LyricsService is { Enabled: true })
            {
                var sidecarService = LyricsService;
                var sidecarPath = outputPath;
                var sidecarSong = song;
                _ = Task.Run(() => sidecarService.TryWriteSidecarAsync(sidecarPath, sidecarSong, CancellationToken.None));
            }

            return outputPath;
        }
        catch
        {
            if (created)
            {
                TryDeleteIncompleteFile(outputPath);
            }
            throw;
        }
    }

    /// <summary>
    /// Copies the provider stream into a scratch file, handing it to <paramref name="onReadable"/>
    /// once the format is known. Returns when the whole track is on disk.
    /// </summary>
    private async Task<ProgressiveDownload> DownloadProgressivelyAsync(DownloadResult result, Song song, string outputPath, Action<ProgressiveDownload> onReadable, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StreamingPath);
        var partPath = Path.Combine(StreamingPath, $"{Guid.NewGuid():N}.part");
        var length = result.ContentLength!.Value;
        ProgressiveDownload? progressive = null;

        ProgressiveDownload Open()
        {
            var extension = DetectAudioExtension(partPath) ?? Path.GetExtension(outputPath);
            var opened = new ProgressiveDownload(partPath, length, Path.ChangeExtension(outputPath, extension), song.Duration);
            onReadable(opened);
            return opened;
        }

        try
        {
            // bufferSize 0: every chunk reaches the OS right away, where readers can see it
            await using (var part = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read | FileShare.Delete, bufferSize: 0, useAsync: true))
            {
                await CopyWithProgressAsync(result.DownloadStream, part, song.Title, cancellationToken, totalBytes =>
                {
                    if (progressive == null && totalBytes >= FormatHeaderBytes)
                    {
                        progressive = Open();
                    }
                    progressive?.ReportProgress(totalBytes);
                });
            }

            // Shorter than a format header: still hand it to any waiting reader
            progressive ??= Open();

            var written = new FileInfo(partPath).Length;
            if (written != length)
            {
                Logger.LogWarning("Download of '{Title}' ended at {Written} bytes, {Expected} were announced",
                    song.Title, written, length);
            }
            progressive.ReportProgress(written);
            progressive.Complete();
            return progressive;
        }
        catch (Exception ex)
        {
            progressive?.Fail(ex);
            TryDeleteStreamingFile(partPath);
            throw;
        }
    }

    /// <summary>
    /// Keeps serving the raw file of a finished download that was streamed for a while, then deletes it.
    /// Readers already streaming keep their open handle after the deletion.
    /// </summary>
    private void KeepFinishedStream(string songId, ProgressiveDownload finished)
    {
        if (_finishedStreams.TryGetValue(songId, out var previous))
        {
            TryDeleteStreamingFile(previous.PartPath);
        }
        _finishedStreams[songId] = finished;
        if (_disposed)
        {
            Dispose();
            return;
        }

        var keepFor = TimeSpan.FromSeconds(finished.DurationSeconds ?? 0) + FinishedStreamMargin;
        _ = Task.Delay(keepFor).ContinueWith(_ =>
        {
            if (_finishedStreams.TryRemove(new KeyValuePair<string, ProgressiveDownload>(songId, finished)))
            {
                TryDeleteStreamingFile(finished.PartPath);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Deletes the raw files still kept for finished streams.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (var songId in _finishedStreams.Keys)
        {
            if (_finishedStreams.TryRemove(songId, out var finished))
            {
                TryDeleteStreamingFile(finished.PartPath);
            }
        }
        GC.SuppressFinalize(this);
    }

    private void TryDeleteStreamingFile(string path)
    {
        try
        {
            IOFile.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete streaming scratch file: {Path}", path);
        }
    }

    private static string? DetectAudioExtension(string path)
    {
        Span<byte> header = stackalloc byte[FormatHeaderBytes];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return DetectAudioExtension(header[..read]);
    }

    /// <summary>
    /// Recognises containers whose extension may be wrong: ".flac" for raw FLAC, ".m4a" for
    /// ISOBMFF/MP4. Null when the header matches neither.
    /// </summary>
    internal static string? DetectAudioExtension(ReadOnlySpan<byte> header)
    {
        // Raw FLAC: starts with fLaC (0x66 0x4C 0x61 0x43)
        if (header.Length >= 4 && header[0] == 0x66 && header[1] == 0x4C && header[2] == 0x61 && header[3] == 0x43)
        {
            return ".flac";
        }
        // ISOBMFF/MP4: bytes 4-7 are 'ftyp' (0x66 0x74 0x79 0x70)
        if (header.Length >= 8 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
        {
            return ".m4a";
        }
        return null;
    }

    // Reads the first bytes of the written file, detects the audio format, and renames
    // the file if the extension doesn't match (e.g. raw FLAC saved as .m4a).
    private string CorrectExtensionIfNeeded(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[FormatHeaderBytes];
            using var f = IOFile.OpenRead(path);
            int read = f.Read(header);
            if (read < 4) return path;

            Logger.LogDebug("Format detection for {File}: first {N} bytes = {Hex}",
                Path.GetFileName(path), read,
                string.Join(" ", header[..read].ToArray().Select(b => b.ToString("X2"))));

            var detectedExt = DetectAudioExtension(header[..read]);
            var currentExt = Path.GetExtension(path).ToLowerInvariant();
            var correctExt = detectedExt != null && detectedExt != currentExt ? detectedExt : null;

            if (correctExt == null) return path;

            var newPath = Path.ChangeExtension(path, correctExt);
            newPath = PathHelper.ResolveUniquePath(newPath);
            IOFile.Move(path, newPath);
            Logger.LogDebug("Renamed {Old} → {New} (detected format mismatch)", Path.GetFileName(path), Path.GetFileName(newPath));
            return newPath;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Format detection failed for {Path}, keeping original extension", path);
            return path;
        }
    }

    // Log progress every 10 MB and cancel if no bytes flow for 90 seconds (stall detection).
    private async Task CopyWithProgressAsync(Stream source, Stream dest, string? title, CancellationToken cancellationToken, Action<long>? onProgress = null)
    {
        const int bufferSize = 81920; // 80 KB
        const long logEveryBytes = 10 * 1024 * 1024; // 10 MB
        const int stallTimeoutSeconds = 90;

        var buffer = new byte[bufferSize];
        long totalBytes = 0;
        long lastLoggedAt = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stallCts.CancelAfter(TimeSpan.FromSeconds(stallTimeoutSeconds));

            int read;
            try
            {
                read = await source.ReadAsync(buffer, stallCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Download stalled for {stallTimeoutSeconds}s on '{title}' after {totalBytes / 1024 / 1024} MB");
            }

            if (read == 0) break;

            await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalBytes += read;
            onProgress?.Invoke(totalBytes);

            if (totalBytes - lastLoggedAt >= logEveryBytes)
            {
                lastLoggedAt = totalBytes;
                Logger.LogDebug("Downloading '{Title}': {MB} MB in {Elapsed}s",
                    title, totalBytes / 1024 / 1024, (int)sw.Elapsed.TotalSeconds);
            }
        }

        Logger.LogInformation("Download complete: '{Title}' — {MB} MB in {Elapsed}s",
            title, totalBytes / 1024 / 1024, (int)sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Writes the known duration into an MP4/M4A file's moov so fragmented MP4 (which stores
    /// timing only in per-fragment boxes) doesn't report a 0:00 length. No-op for other formats.
    /// Failures are logged but never abort the download — the audio is already on disk.
    /// </summary>
    private void PatchMp4DurationIfNeeded(string outputPath, DownloadResult result)
    {
        if (result.Mp4DurationSeconds is not > 0)
        {
            return;
        }

        var ext = Path.GetExtension(outputPath);
        if (!ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (Mp4DurationPatcher.PatchDuration(outputPath, result.Mp4DurationSeconds.Value))
            {
                Logger.LogInformation("Patched MP4 duration ({Duration:F3}s) for {Path}", result.Mp4DurationSeconds.Value, outputPath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to patch MP4 duration for {Path}", outputPath);
        }
    }

    protected async Task DownloadRemainingAlbumTracksAsync(string albumExternalId, string excludeTrackExternalId, CancellationToken cancellationToken = default)
    {
        await DownloadAlbumTracksAsync(albumExternalId, excludeTrackExternalId, forcePermanent: false, cancellationToken);
    }

    protected async Task DownloadFullAlbumAsync(string albumExternalId, bool forcePermanent = false, CancellationToken cancellationToken = default)
    {
        await DownloadAlbumTracksAsync(albumExternalId, excludeTrackExternalId: null, forcePermanent, cancellationToken);
    }

    private async Task DownloadAlbumTracksAsync(string albumExternalId, string? excludeTrackExternalId, bool forcePermanent = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(excludeTrackExternalId))
        {
            Logger.LogInformation("Starting background full album download for album {AlbumId}", albumExternalId);
        }
        else
        {
            Logger.LogInformation("Starting background download for album {AlbumId} (excluding track {TrackId})",
                albumExternalId, excludeTrackExternalId);
        }

        var album = await MetadataService.GetAlbumAsync(ProviderName, albumExternalId);
        if (album == null)
        {
            Logger.LogWarning("Album {AlbumId} not found, cannot download tracks", albumExternalId);
            return;
        }

        var tracksToDownload = album.Songs
            .Where(s => !string.IsNullOrEmpty(s.ExternalId) && (string.IsNullOrEmpty(excludeTrackExternalId) || s.ExternalId != excludeTrackExternalId))
            .ToList();

        Logger.LogInformation("Found {Count} tracks to download for album '{AlbumTitle}'",
            tracksToDownload.Count, album.Title);

        foreach (var track in tracksToDownload)
        {
            try
            {
                var existingPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(ProviderName, track.ExternalId!);
                if (existingPath != null && IOFile.Exists(existingPath))
                {
                    Logger.LogDebug("Track {TrackId} already in library, skipping", track.ExternalId);
                    await UpgradeQualityAsync(track.ExternalId!, triggerAlbumDownload: false, cancellationToken);
                    continue;
                }

                // Try to permanentize cached track BEFORE checking for completed downloads
                // otherwise permanentization would be skipped as we already have a completed cache download
                if (SubsonicSettings.StorageMode == StorageMode.Cache && forcePermanent)
                {
                    var permanentized = await PermanentizeCachedSongAsync(ProviderName, track.ExternalId!, cancellationToken);
                    if (permanentized)
                    {
                        Logger.LogInformation("Permanentized cached track '{Title}' from album '{Album}'", track.Title, album.Title);
                        continue;
                    }
                }

                // Check if download is already in progress or recently completed
                var songId = $"ext-{ProviderName}-{track.ExternalId}";
                if (ActiveDownloads.TryGetValue(songId, out var activeDownload))
                {
                    if (activeDownload.Status == DownloadStatus.InProgress)
                    {
                        Logger.LogDebug("Track {TrackId} download already in progress, skipping", track.ExternalId);
                        continue;
                    }

                    if (activeDownload.Status == DownloadStatus.Completed)
                    {
                        Logger.LogDebug("Track {TrackId} already downloaded in this session, skipping", track.ExternalId);
                        continue;
                    }
                }

                Logger.LogInformation("Downloading track '{Title}' from album '{Album}'", track.Title, album.Title);
                // The album listing already carries the track's full metadata, album artist included
                await DownloadSongInternalAsync(ProviderName, track.ExternalId!, triggerAlbumDownload: false, forcePermanent, cancellationToken, metadata: track);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to download track {TrackId} '{Title}'", track.ExternalId, track.Title);
            }
        }

        Logger.LogInformation("Completed background download for album '{AlbumTitle}'", album.Title);
    }

    #endregion

    #region Common Metadata Writing

    /// <summary>
    /// Writes ID3/Vorbis metadata and cover art to the audio file
    /// </summary>
    protected async Task WriteMetadataAsync(string filePath, Song song, CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogInformation("Writing metadata to: {Path}", filePath);

            using var tagFile = TagLib.File.Create(filePath);
            Logger.LogDebug("TagLib opened {File} as MIME type: {MimeType}", Path.GetFileName(filePath), tagFile.MimeType);

            // Basic metadata
            tagFile.Tag.Title = song.Title;
            tagFile.Tag.Performers = song.Artists.Count > 0
                ? song.Artists.Select(a => a.Name).ToArray()
                : new[] { song.Artist };
            tagFile.Tag.Album = song.Album;
            tagFile.Tag.AlbumArtists = new[] { !string.IsNullOrEmpty(song.AlbumArtist) ? song.AlbumArtist : song.Artist };

            if (song.Track.HasValue)
                tagFile.Tag.Track = (uint)song.Track.Value;

            if (song.TotalTracks.HasValue)
                tagFile.Tag.TrackCount = (uint)song.TotalTracks.Value;

            if (song.DiscNumber.HasValue)
                tagFile.Tag.Disc = (uint)song.DiscNumber.Value;

            if (song.Year.HasValue)
                tagFile.Tag.Year = (uint)song.Year.Value;

            if (!string.IsNullOrEmpty(song.Genre))
                tagFile.Tag.Genres = new[] { song.Genre };

            if (song.Bpm.HasValue)
                tagFile.Tag.BeatsPerMinute = (uint)song.Bpm.Value;

            if (song.Contributors.Count > 0)
                tagFile.Tag.Composers = song.Contributors.ToArray();

            if (!string.IsNullOrEmpty(song.Copyright))
                tagFile.Tag.Copyright = song.Copyright;

            if (!string.IsNullOrEmpty(song.ReleaseType))
                tagFile.Tag.MusicBrainzReleaseType = song.ReleaseType;

            NativeMetadataTags.Write(tagFile, song);

            var comments = new List<string>();
            if (!string.IsNullOrEmpty(song.Isrc))
                comments.Add($"ISRC: {song.Isrc}");

            if (comments.Count > 0)
                tagFile.Tag.Comment = string.Join(" | ", comments);

            // Download and embed cover art
            var coverUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl;
            if (!string.IsNullOrEmpty(coverUrl))
            {
                // Local helper to determine MIME type from image data.
                static string GetImageMimeTypeFromData(byte[] data)
                {
                    if (data == null || data.Length == 0)
                        return "image/jpeg";

                    // PNG signature: 89 50 4E 47 0D 0A 1A 0A
                    if (data.Length >= 8 &&
                        data[0] == 0x89 &&
                        data[1] == 0x50 &&
                        data[2] == 0x4E &&
                        data[3] == 0x47 &&
                        data[4] == 0x0D &&
                        data[5] == 0x0A &&
                        data[6] == 0x1A &&
                        data[7] == 0x0A)
                    {
                        return "image/png";
                    }

                    // JPEG signature: FF D8 FF
                    if (data.Length >= 3 &&
                        data[0] == 0xFF &&
                        data[1] == 0xD8 &&
                        data[2] == 0xFF)
                    {
                        return "image/jpeg";
                    }

                    // GIF signature: "GIF"
                    if (data.Length >= 3 &&
                        data[0] == 0x47 && // 'G'
                        data[1] == 0x49 && // 'I'
                        data[2] == 0x46)   // 'F'
                    {
                        return "image/gif";
                    }

                    // Fallback to JPEG to preserve previous behavior.
                    return "image/jpeg";
                }

                try
                {
                    var coverData = await DownloadCoverArtAsync(coverUrl, cancellationToken);
                    if (coverData != null && coverData.Length > 0)
                    {
                        var mimeType = GetImageMimeTypeFromData(coverData);
                        var picture = new TagLib.Picture
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = mimeType,
                            Description = "Cover",
                            Data = new TagLib.ByteVector(coverData)
                        };
                        tagFile.Tag.Pictures = new TagLib.IPicture[] { picture };
                        Logger.LogInformation("Cover art embedded: {Size} bytes", coverData.Length);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to download cover art from {Url}", coverUrl);
                }
            }

            tagFile.Save();
            Logger.LogInformation("Metadata written successfully to: {Path}", filePath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to write metadata to: {Path}", filePath);
        }
    }

    /// <summary>
    /// Downloads cover art from a URL
    /// </summary>
    protected async Task<byte[]?> DownloadCoverArtAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("CoverArtClient");
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to download cover art from {Url}", url);
            return null;
        }
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Ensures a directory exists, creating it and all parent directories if necessary
    /// </summary>
    protected void EnsureDirectoryExists(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                Logger.LogDebug("Created directory: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create directory: {Path}", path);
            throw;
        }
    }

    /// <summary>
    /// Deletes an incomplete file after canceled/failed downloads.
    /// </summary>
    protected void TryDeleteIncompleteFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !IOFile.Exists(filePath))
        {
            return;
        }

        try
        {
            IOFile.Delete(filePath);
            Logger.LogInformation("Deleted incomplete file: {Path}", filePath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete incomplete file: {Path}", filePath);
        }
    }

    /// <summary>
    /// Gets the cached file path for a given provider and external ID
    /// Returns null if no cached file exists
    /// </summary>
    protected async Task<string?> GetCachedFilePathAsync(string provider, string externalId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Legacy cache naming scheme: {provider}_{externalId}.*
            var legacyPattern = $"{provider}_{externalId}.*";
            var legacyFiles = Directory.GetFiles(CachePath, legacyPattern, SearchOption.AllDirectories);

            if (legacyFiles.Length > 0)
            {
                return legacyFiles[0];
            }

            CleanupExpiredMetadataCacheEntries();

            // Use an in-memory cache to avoid repeated metadata lookups for hot keys.
            var cacheKey = $"{provider}|{externalId}";
            if (_metadataPathCache.TryGetValue(cacheKey, out var entry) && entry.Expiry > DateTime.UtcNow)
            {
                return entry.Path;
            }

            // Ensure only one concurrent metadata lookup/update for the same key.
            var sem = _metadataPathLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(cancellationToken);

            try
            {
                // Re-check cache after acquiring lock in case another waiter populated it.
                if (_metadataPathCache.TryGetValue(cacheKey, out entry) && entry.Expiry > DateTime.UtcNow)
                {
                    return entry.Path;
                }

                var found = await FindCachedPathFromMetadataAsync(provider, externalId, cancellationToken);

                var ttl = found != null ? MetadataCacheTtl : MetadataCacheNegativeTtl;
                _metadataPathCache[cacheKey] = (found, DateTime.UtcNow.Add(ttl));

                return found;
            }
            finally
            {
                try
                {
                    sem.Release();
                }
                catch (ObjectDisposedException)
                {
                    // If the semaphore was disposed concurrently, ignore.
                }

                // Opportunistic cleanup: if the semaphore shows no waiters and the dictionary still
                // contains the same instance, remove and dispose it to avoid unbounded growth.
                TryRemoveAndDisposeSemaphore(cacheKey, sem);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to search for cached file: {Provider}_{ExternalId}", provider, externalId);
            return null;
        }
    }

    private async Task<string?> FindCachedPathFromMetadataAsync(string provider, string externalId, CancellationToken cancellationToken)
    {
        try
        {
            var song = await MetadataService.GetSongAsync(provider, externalId);
            if (song == null)
            {
                Logger.LogDebug("No song metadata found for {Provider}_{ExternalId} during cache lookup.", provider, externalId);
                return null;
            }

            // If AlbumArtist is not set but we have an AlbumId, fetch the album to get the correct AlbumArtist.
            // This ensures cache lookup uses the same path as when the file was originally downloaded.
            if (string.IsNullOrEmpty(song.AlbumArtist) && !string.IsNullOrEmpty(song.AlbumId))
            {
                var albumExternalId = ExtractExternalIdFromAlbumId(song.AlbumId);
                if (!string.IsNullOrEmpty(albumExternalId))
                {
                    var album = await MetadataService.GetAlbumAsync(provider, albumExternalId);
                    if (album != null)
                    {
                        // Find the track in the album to get full metadata including AlbumArtist
                        var albumSong = album.Songs.FirstOrDefault(s => s.ExternalId == externalId);
                        if (albumSong != null)
                        {
                            song = albumSong;
                        }
                        else
                        {
                            // Use album artist even if track not found in album
                            song.AlbumArtist = album.Artist;
                        }
                    }
                }
            }

            var artistForPath = song.AlbumArtist ?? song.Artist;

            // Build the expected file name from the last segment of the template.
            // We don't know the quality at lookup time, so we search by file name pattern
            // within the entire cache directory tree to handle any folder template.
            var template = SubsonicSettings.FolderTemplate;
            var templateSegments = template.Split('/');
            var fileNameTemplate = templateSegments[^1];

            var expectedFileName = PathHelper.ReplacePlaceholders(fileNameTemplate, song, artistForPath, null);
            var safeFileName = PathHelper.SanitizeFileName(expectedFileName);

            // Search from the cache root for any file matching the expected name,
            // regardless of the folder structure used by the template.
            var searchPattern = $"{safeFileName}*.*";
            var matches = Directory.GetFiles(CachePath, searchPattern, SearchOption.AllDirectories);
            if (matches.Length > 0)
            {
                return matches[0];
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed metadata-based cache lookup for {Provider}_{ExternalId}", provider, externalId);
            return null;
        }
    }

    private void CleanupExpiredMetadataCacheEntries()
    {
        var now = DateTime.UtcNow;
        if (now < _metadataCacheNextCleanupUtc)
        {
            return;
        }

        lock (_metadataCacheCleanupLock)
        {
            if (now < _metadataCacheNextCleanupUtc)
            {
                return;
            }

            foreach (var kvp in _metadataPathCache.ToArray())
            {
                if (kvp.Value.Expiry <= now)
                {
                    _metadataPathCache.TryRemove(kvp.Key, out _);

                    // Try to remove and dispose any associated semaphore. This is opportunistic; if the
                    // semaphore is currently in use the TryRemove will fail or the instance won't match.
                    if (_metadataPathLocks.TryRemove(kvp.Key, out var removedSem))
                    {
                        try
                        {
                            removedSem.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "Failed to dispose semaphore for {CacheKey}", kvp.Key);
                        }
                    }
                }
            }

            _metadataCacheNextCleanupUtc = now.Add(MetadataCacheCleanupInterval);
        }
    }

    private void TryRemoveAndDisposeSemaphore(string cacheKey, SemaphoreSlim sem)
    {
        try
        {
            // Opportunistically attempt to remove and dispose the semaphore if the dictionary
            // currently references this exact instance. This avoids relying on CurrentCount,
            // which is inherently racy in concurrent scenarios.
            if (_metadataPathLocks.TryGetValue(cacheKey, out var existing) && ReferenceEquals(existing, sem))
            {
                if (_metadataPathLocks.TryRemove(cacheKey, out var removed) && ReferenceEquals(removed, sem))
                {
                    try
                    {
                        removed.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "Failed to dispose semaphore for {CacheKey}", cacheKey);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Semaphore cleanup check failed for {CacheKey}", cacheKey);
        }
    }

    #endregion
}
