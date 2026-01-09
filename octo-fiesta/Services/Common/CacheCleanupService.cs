using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Background service that periodically cleans up old cached files
/// Only runs when StorageMode is set to Cache
/// </summary>
public class CacheCleanupService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<CacheCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public CacheCleanupService(
        IConfiguration configuration,
        IOptions<SubsonicSettings> subsonicSettings,
        ILogger<CacheCleanupService> logger)
    {
        _configuration = configuration;
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only run if storage mode is Cache
        if (_subsonicSettings.StorageMode != StorageMode.Cache)
        {
            _logger.LogInformation("CacheCleanupService disabled: StorageMode is not Cache");
            return;
        }

        _logger.LogInformation("CacheCleanupService started with cleanup interval of {Interval} and retention of {Hours} hours",
            _cleanupInterval, _subsonicSettings.CacheDurationHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOldCachedFilesAsync(stoppingToken);
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service is stopping, exit gracefully
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache cleanup");
                // Continue running even if cleanup fails
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
        }

        _logger.LogInformation("CacheCleanupService stopped");
    }

    private async Task CleanupOldCachedFilesAsync(CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "octo-fiesta-cache");

        if (!Directory.Exists(cachePath))
        {
            _logger.LogDebug("Cache directory does not exist: {Path}", cachePath);
            return;
        }

        var cutoffTime = DateTime.UtcNow.AddHours(-_subsonicSettings.CacheDurationHours);
        var deletedCount = 0;
        var totalSize = 0L;

        _logger.LogInformation("Starting cache cleanup: deleting files older than {CutoffTime}", cutoffTime);

        try
        {
            // Get all files in cache directory and subdirectories
            var files = Directory.GetFiles(cachePath, "*.*", SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var fileInfo = new FileInfo(filePath);

                    // Use last access time to determine if file should be deleted
                    // This gets updated when a cached file is streamed
                    if (fileInfo.LastAccessTimeUtc < cutoffTime)
                    {
                        var size = fileInfo.Length;
                        File.Delete(filePath);
                        deletedCount++;
                        totalSize += size;
                        _logger.LogDebug("Deleted cached file: {Path} (last accessed: {LastAccess})",
                            filePath, fileInfo.LastAccessTimeUtc);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete cached file: {Path}", filePath);
                }
            }

            // Clean up empty directories
            await CleanupEmptyDirectoriesAsync(cachePath, cancellationToken);

            if (deletedCount > 0)
            {
                var sizeMB = totalSize / (1024.0 * 1024.0);
                _logger.LogInformation("Cache cleanup completed: deleted {Count} files, freed {Size:F2} MB",
                    deletedCount, sizeMB);
            }
            else
            {
                _logger.LogDebug("Cache cleanup completed: no files to delete");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }
    }

    private async Task CleanupEmptyDirectoriesAsync(string rootPath, CancellationToken cancellationToken)
    {
        try
        {
            var directories = Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length); // Process deepest directories first

            foreach (var directory in directories)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                        _logger.LogDebug("Deleted empty directory: {Path}", directory);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete empty directory: {Path}", directory);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up empty directories");
        }

        await Task.CompletedTask;
    }
}
