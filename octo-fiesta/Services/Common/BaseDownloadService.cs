using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Local;
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
public abstract class BaseDownloadService : IDownloadService
{
    protected readonly IConfiguration Configuration;
    protected readonly ILocalLibraryService LocalLibraryService;
    protected readonly IMusicMetadataService MetadataService;
    protected readonly SubsonicSettings SubsonicSettings;
    protected readonly ILogger Logger;
    private readonly IServiceProvider _serviceProvider;

    protected readonly string DownloadPath;
    protected readonly string CachePath;

    protected readonly Dictionary<string, DownloadInfo> ActiveDownloads = new();
    protected readonly SemaphoreSlim DownloadLock = new(1, 1);

    // Small in-memory cache to avoid repeated metadata/network lookups when searching for cached files.
    // Key: "{provider}|{externalId}" -> (path or null, expiry)
    private readonly ConcurrentDictionary<string, (string? Path, DateTime Expiry)> _metadataPathCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataPathLocks = new();
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

        DownloadPath = configuration["Library:DownloadPath"] ?? "./downloads";
        CachePath = PathHelper.GetCachePath();

        if (!Directory.Exists(DownloadPath))
        {
            Directory.CreateDirectory(DownloadPath);
        }

        if (!Directory.Exists(CachePath))
        {
            Directory.CreateDirectory(CachePath);
        }
    }

    #region IDownloadService Implementation

    public async Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, cancellationToken);
    }

    public async Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var localPath = await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, cancellationToken);
        return IOFile.OpenRead(localPath);
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

    #endregion

    #region Template Methods (to be implemented by subclasses)

    /// <summary>
    /// Result of a track download containing path and quality info
    /// </summary>
    public record DownloadResult(string LocalPath, string? DownloadedQuality);

    /// <summary>
    /// Downloads a track and saves it to disk.
    /// Subclasses implement provider-specific logic (encryption, authentication, etc.)
    /// </summary>
    /// <param name="trackId">External track ID</param>
    /// <param name="song">Song metadata</param>
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

    #endregion

    #region Common Download Logic

    /// <summary>
    /// Internal method for downloading a song with control over album download triggering
    /// </summary>
    protected async Task<string> DownloadSongInternalAsync(string externalProvider, string externalId, bool triggerAlbumDownload, CancellationToken cancellationToken = default)
    {
        if (externalProvider != ProviderName)
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        var songId = $"ext-{externalProvider}-{externalId}";
        var isCache = SubsonicSettings.StorageMode == StorageMode.Cache;

        // Acquire lock BEFORE checking existence to prevent race conditions with concurrent requests
        await DownloadLock.WaitAsync(cancellationToken);

        try
        {
            // If we create an ActiveDownloads entry for a quality-upgrade, keep a reference
            // to that instance so we can identify our own marker later without special flags.
            DownloadInfo? ourDownloadInfo = null;
            // Check if already downloaded (skip for cache mode as we want to check cache folder)
            if (!isCache)
            {
                var existingMapping = await LocalLibraryService.GetMappingForExternalSongAsync(externalProvider, externalId);
                if (existingMapping != null && IOFile.Exists(existingMapping.LocalPath))
                {
                    // Check if we should upgrade quality
                    var targetQuality = GetTargetQuality();
                    var shouldUpgrade = QualityHelper.ShouldUpgrade(existingMapping.DownloadedQuality, targetQuality);

                    if (SubsonicSettings.AutoUpgradeQuality && shouldUpgrade)
                    {
                        // Check if another upgrade is already in progress for this song
                        if (ActiveDownloads.TryGetValue(songId, out var existingDownload) && existingDownload.Status == DownloadStatus.InProgress)
                        {
                            Logger.LogInformation("Upgrade already in progress for {SongId}, waiting...", songId);
                            DownloadLock.Release();

                            while (ActiveDownloads.TryGetValue(songId, out existingDownload) && existingDownload.Status == DownloadStatus.InProgress)
                            {
                                await Task.Delay(500, cancellationToken);
                            }

                            if (existingDownload?.Status == DownloadStatus.Completed && existingDownload.LocalPath != null)
                            {
                                return existingDownload.LocalPath;
                            }

                            throw new Exception(existingDownload?.ErrorMessage ?? "Upgrade failed");
                        }

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
                            return existingMapping.LocalPath;
                        }

                        // Store backup path to restore on failure - register an in-progress marker so other callers know an upgrade is in progress. Keep a reference to our marker.
                        var upgradeInfo = new DownloadInfo
                        {
                            SongId = songId,
                            ExternalId = externalId,
                            ExternalProvider = externalProvider,
                            Status = DownloadStatus.InProgress,
                            StartedAt = DateTime.UtcNow,
                            BackupPath = backupPath
                        };
                        ActiveDownloads[songId] = upgradeInfo;
                        ourDownloadInfo = upgradeInfo;
                    }
                    else
                    {
                        Logger.LogInformation("Song already downloaded: {Path}", existingMapping.LocalPath);
                        return existingMapping.LocalPath;
                    }
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
                    return cachedPath;
                }
            }

            // Check if download in progress. If the in-progress marker belongs to our
            // current upgrade flow (ourDownloadInfo), do not wait on it.
            if (ActiveDownloads.TryGetValue(songId, out var activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
            {
                if (ourDownloadInfo != null && ReferenceEquals(activeDownload, ourDownloadInfo))
                {
                    // This is our own marker created for a quality-upgrade; proceed without waiting.
                }
                else
                {
                    Logger.LogInformation("Download already in progress for {SongId}, waiting...", songId);
                    // Release lock while waiting
                    DownloadLock.Release();

                    while (ActiveDownloads.TryGetValue(songId, out activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
                    {
                        await Task.Delay(500, cancellationToken);
                    }

                    if (activeDownload?.Status == DownloadStatus.Completed && activeDownload.LocalPath != null)
                    {
                        return activeDownload.LocalPath;
                    }

                    throw new Exception(activeDownload?.ErrorMessage ?? "Download failed");
                }
            }
            // Get metadata
            // In Album mode, fetch the full album first to ensure AlbumArtist is correctly set
            Song? song = null;

            if (SubsonicSettings.DownloadMode == DownloadMode.Album)
            {
                // First try to get the song to extract album ID
                var tempSong = await MetadataService.GetSongAsync(externalProvider, externalId);
                if (tempSong != null && !string.IsNullOrEmpty(tempSong.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(tempSong.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        // Get full album with correct AlbumArtist
                        var album = await MetadataService.GetAlbumAsync(externalProvider, albumExternalId);
                        if (album != null)
                        {
                            // Find the track in the album
                            song = album.Songs.FirstOrDefault(s => s.ExternalId == externalId);
                        }
                    }
                }
            }

            // Fallback to individual song fetch if not in Album mode or album fetch failed
            if (song == null)
            {
                song = await MetadataService.GetSongAsync(externalProvider, externalId);
            }

            if (song == null)
            {
                throw new Exception("Song not found");
            }

            // Only create new DownloadInfo if not already created (e.g., by upgrade logic)
            if (!ActiveDownloads.TryGetValue(songId, out var downloadInfo))
            {
                downloadInfo = new DownloadInfo
                {
                    SongId = songId,
                    ExternalId = externalId,
                    ExternalProvider = externalProvider,
                    Status = DownloadStatus.InProgress,
                    StartedAt = DateTime.UtcNow
                };
                ActiveDownloads[songId] = downloadInfo;
            }

            var downloadResult = await DownloadTrackAsync(externalId, song, cancellationToken);
            var localPath = downloadResult.LocalPath;

            downloadInfo.Status = DownloadStatus.Completed;
            downloadInfo.LocalPath = localPath;
            downloadInfo.CompletedAt = DateTime.UtcNow;

            // Invalidate the metadata path cache so subsequent requests find the newly downloaded file
            var cacheKey = $"{externalProvider}|{externalId}";
            _metadataPathCache.TryRemove(cacheKey, out _);

            song.LocalPath = localPath;

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
            }
            else
            {
                Logger.LogInformation("Cache mode: skipping library registration and scan");
            }

            Logger.LogInformation("Download completed: {Path}", localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            if (ActiveDownloads.TryGetValue(songId, out var downloadInfo))
            {
                downloadInfo.Status = DownloadStatus.Failed;
                downloadInfo.ErrorMessage = ex.Message;

                // Restore backup if quality upgrade failed
                if (!string.IsNullOrEmpty(downloadInfo.BackupPath) && IOFile.Exists(downloadInfo.BackupPath))
                {
                    try
                    {
                        var originalPath = downloadInfo.BackupPath.Replace(".backup", "");
                        IOFile.Move(downloadInfo.BackupPath, originalPath);
                        Logger.LogInformation("Restored backup after failed quality upgrade: {Path}", originalPath);
                    }
                    catch (Exception restoreEx)
                    {
                        Logger.LogError(restoreEx, "Failed to restore backup file: {BackupPath}", downloadInfo.BackupPath);
                    }
                }
            }
            Logger.LogError(ex, "Download failed for {SongId}", songId);
            throw;
        }
        finally
        {
            // Clean up backup file on success
            if (ActiveDownloads.TryGetValue(songId, out var info) &&
                info.Status == DownloadStatus.Completed &&
                !string.IsNullOrEmpty(info.BackupPath) &&
                IOFile.Exists(info.BackupPath))
            {
                try
                {
                    IOFile.Delete(info.BackupPath);
                    Logger.LogInformation("Deleted backup after successful quality upgrade");
                }
                catch (Exception deleteEx)
                {
                    Logger.LogWarning(deleteEx, "Failed to delete backup file: {BackupPath}", info.BackupPath);
                }
            }

            DownloadLock.Release();
        }
    }

    protected async Task DownloadRemainingAlbumTracksAsync(string albumExternalId, string excludeTrackExternalId, CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Starting background download for album {AlbumId} (excluding track {TrackId})",
            albumExternalId, excludeTrackExternalId);

        var album = await MetadataService.GetAlbumAsync(ProviderName, albumExternalId);
        if (album == null)
        {
            Logger.LogWarning("Album {AlbumId} not found, cannot download remaining tracks", albumExternalId);
            return;
        }

        var tracksToDownload = album.Songs
            .Where(s => s.ExternalId != excludeTrackExternalId && !string.IsNullOrEmpty(s.ExternalId))
            .ToList();

        Logger.LogInformation("Found {Count} additional tracks to download for album '{AlbumTitle}'",
            tracksToDownload.Count, album.Title);

        foreach (var track in tracksToDownload)
        {
            try
            {
                var existingPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(ProviderName, track.ExternalId!);
                if (existingPath != null && IOFile.Exists(existingPath))
                {
                    Logger.LogDebug("Track {TrackId} already downloaded, skipping", track.ExternalId);
                    continue;
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
                await DownloadSongInternalAsync(ProviderName, track.ExternalId!, triggerAlbumDownload: false, cancellationToken);
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

            // Basic metadata
            tagFile.Tag.Title = song.Title;
            tagFile.Tag.Performers = new[] { song.Artist };
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
            var safeArtist = PathHelper.SanitizeFolderName(artistForPath);
            var safeAlbum = PathHelper.SanitizeFolderName(song.Album);
            var safeTitle = PathHelper.SanitizeFileName(song.Title);

            var albumFolder = Path.Combine(CachePath, safeArtist, safeAlbum);
            if (!Directory.Exists(albumFolder))
            {
                return null;
            }

            var trackPrefix = song.Track.HasValue ? $"{song.Track.Value:D2} - " : string.Empty;

            // Prefer exact expected prefix, but allow duplicates resolved by " (n)" suffix.
            var primaryPattern = $"{trackPrefix}{safeTitle}*.*";
            var primaryMatches = Directory.GetFiles(albumFolder, primaryPattern, SearchOption.TopDirectoryOnly);
            if (primaryMatches.Length > 0)
            {
                return primaryMatches[0];
            }

            // If track numbers differ/missing, try title-only match within the same album folder.
            if (!string.IsNullOrEmpty(trackPrefix))
            {
                var fallbackPattern = $"{safeTitle}*.*";
                var fallbackMatches = Directory.GetFiles(albumFolder, fallbackPattern, SearchOption.TopDirectoryOnly);
                if (fallbackMatches.Length > 0)
                {
                    return fallbackMatches[0];
                }
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
