using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Local;
using TagLib;
using IOFile = System.IO.File;

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
    
    protected readonly string DownloadPath;
    protected readonly string CachePath;
    
    protected readonly Dictionary<string, DownloadInfo> ActiveDownloads = new();
    protected readonly SemaphoreSlim DownloadLock = new(1, 1);
    
    /// <summary>
    /// Provider name (e.g., "deezer", "qobuz")
    /// </summary>
    protected abstract string ProviderName { get; }
    
    protected BaseDownloadService(
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        SubsonicSettings subsonicSettings,
        ILogger logger)
    {
        Configuration = configuration;
        LocalLibraryService = localLibraryService;
        MetadataService = metadataService;
        SubsonicSettings = subsonicSettings;
        Logger = logger;
        
        DownloadPath = configuration["Library:DownloadPath"] ?? "./downloads";
        CachePath = Path.Combine(Path.GetTempPath(), "octo-fiesta-cache");
        
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
    /// Downloads a track and saves it to disk.
    /// Subclasses implement provider-specific logic (encryption, authentication, etc.)
    /// </summary>
    /// <param name="trackId">External track ID</param>
    /// <param name="song">Song metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Local file path where the track was saved</returns>
    protected abstract Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken);
    
    /// <summary>
    /// Extracts the external album ID from the internal album ID format.
    /// Example: "ext-deezer-album-123456" -> "123456"
    /// </summary>
    protected abstract string? ExtractExternalIdFromAlbumId(string albumId);
    
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
        
        // Check if already downloaded (skip for cache mode as we want to check cache folder)
        if (!isCache)
        {
            var existingPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
            if (existingPath != null && IOFile.Exists(existingPath))
            {
                Logger.LogInformation("Song already downloaded: {Path}", existingPath);
                return existingPath;
            }
        }
        else
        {
            // For cache mode, check if file exists in cache directory
            var cachedPath = GetCachedFilePath(externalProvider, externalId);
            if (cachedPath != null && IOFile.Exists(cachedPath))
            {
                Logger.LogInformation("Song found in cache: {Path}", cachedPath);
                // Update file access time for cache cleanup logic
                IOFile.SetLastAccessTime(cachedPath, DateTime.UtcNow);
                return cachedPath;
            }
        }

        // Check if download in progress
        if (ActiveDownloads.TryGetValue(songId, out var activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
        {
            Logger.LogInformation("Download already in progress for {SongId}", songId);
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

        await DownloadLock.WaitAsync(cancellationToken);
        try
        {
            // Get metadata
            var song = await MetadataService.GetSongAsync(externalProvider, externalId);
            if (song == null)
            {
                throw new Exception("Song not found");
            }

            var downloadInfo = new DownloadInfo
            {
                SongId = songId,
                ExternalId = externalId,
                ExternalProvider = externalProvider,
                Status = DownloadStatus.InProgress,
                StartedAt = DateTime.UtcNow
            };
            ActiveDownloads[songId] = downloadInfo;

            try
            {
                var localPath = await DownloadTrackAsync(externalId, song, cancellationToken);
                
                downloadInfo.Status = DownloadStatus.Completed;
                downloadInfo.LocalPath = localPath;
                downloadInfo.CompletedAt = DateTime.UtcNow;
                
                song.LocalPath = localPath;
                
                // Only register and scan if NOT in cache mode
                if (!isCache)
                {
                    await LocalLibraryService.RegisterDownloadedSongAsync(song, localPath);
                    
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
                downloadInfo.Status = DownloadStatus.Failed;
                downloadInfo.ErrorMessage = ex.Message;
                Logger.LogError(ex, "Download failed for {SongId}", songId);
                throw;
            }
        }
        finally
        {
            DownloadLock.Release();
        }
    }
    
    protected async Task DownloadRemainingAlbumTracksAsync(string albumExternalId, string excludeTrackExternalId)
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

                Logger.LogInformation("Downloading track '{Title}' from album '{Album}'", track.Title, album.Title);
                await DownloadSongInternalAsync(ProviderName, track.ExternalId!, triggerAlbumDownload: false, CancellationToken.None);
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
                try
                {
                    var coverData = await DownloadCoverArtAsync(coverUrl, cancellationToken);
                    if (coverData != null && coverData.Length > 0)
                    {
                        var mimeType = coverUrl.Contains(".png") ? "image/png" : "image/jpeg";
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
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(url, cancellationToken);
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
    protected string? GetCachedFilePath(string provider, string externalId)
    {
        try
        {
            // Search for cached files matching the pattern: {provider}_{externalId}.*
            var pattern = $"{provider}_{externalId}.*";
            var files = Directory.GetFiles(CachePath, pattern, SearchOption.AllDirectories);
            
            if (files.Length > 0)
            {
                return files[0]; // Return first match
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to search for cached file: {Provider}_{ExternalId}", provider, externalId);
            return null;
        }
    }
    
    #endregion
}
