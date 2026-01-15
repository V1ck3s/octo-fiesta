using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.Subsonic;

/// <summary>
/// Service responsible for downloading playlist tracks and creating M3U files
/// </summary>
public class PlaylistSyncService
{
    private readonly IMusicMetadataService _deezerMetadataService;
    private readonly IMusicMetadataService _qobuzMetadataService;
    private readonly IEnumerable<IDownloadService> _downloadServices;
    private readonly IConfiguration _configuration;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<PlaylistSyncService> _logger;
    
    // In-memory cache to track which playlist a track belongs to
    // Key: trackId (format: ext-{provider}-{externalId}), Value: playlistId
    // TTL: 5 minutes (tracks expire automatically)
    private readonly ConcurrentDictionary<string, (string PlaylistId, DateTime ExpiresAt)> _trackPlaylistCache = new();
    private static readonly TimeSpan CacheTTL = TimeSpan.FromMinutes(5);
    
    private readonly string _musicDirectory;
    private readonly string _playlistDirectory;
    
    // Cancellation token for background cleanup task
    private readonly CancellationTokenSource _cleanupCancellationTokenSource = new();
    private readonly Task _cleanupTask;
    
    public PlaylistSyncService(
        IEnumerable<IMusicMetadataService> metadataServices,
        IEnumerable<IDownloadService> downloadServices,
        IConfiguration configuration,
        IOptions<SubsonicSettings> subsonicSettings,
        ILogger<PlaylistSyncService> logger)
    {
        // Get Deezer and Qobuz metadata services
        _deezerMetadataService = metadataServices.FirstOrDefault(s => s.GetType().Name.Contains("Deezer"))
            ?? throw new InvalidOperationException("Deezer metadata service not found");
        _qobuzMetadataService = metadataServices.FirstOrDefault(s => s.GetType().Name.Contains("Qobuz"))
            ?? throw new InvalidOperationException("Qobuz metadata service not found");
        
        _downloadServices = downloadServices;
        _configuration = configuration;
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;
        
        _musicDirectory = configuration["Library:DownloadPath"] ?? "./downloads";
        _playlistDirectory = Path.Combine(_musicDirectory, _subsonicSettings.PlaylistsDirectory ?? "playlists");
        
        // Ensure playlists directory exists
        if (!Directory.Exists(_playlistDirectory))
        {
            Directory.CreateDirectory(_playlistDirectory);
        }
        
        // Start background cleanup task for expired cache entries
        _cleanupTask = Task.Run(() => CleanupExpiredCacheEntriesAsync(_cleanupCancellationTokenSource.Token));
    }
    
    /// <summary>
    /// Gets the metadata service for the specified provider
    /// </summary>
    private IMusicMetadataService? GetMetadataServiceForProvider(string provider)
    {
        return provider.ToLower() switch
        {
            "deezer" => _deezerMetadataService,
            "qobuz" => _qobuzMetadataService,
            _ => null
        };
    }
    
    /// <summary>
    /// Adds a track to the playlist context cache.
    /// This allows the download service to know which playlist a track belongs to.
    /// </summary>
    public void AddTrackToPlaylistCache(string trackId, string playlistId)
    {
        var expiresAt = DateTime.UtcNow.Add(CacheTTL);
        _trackPlaylistCache[trackId] = (playlistId, expiresAt);
        _logger.LogInformation("Added track {TrackId} to playlist cache with playlistId {PlaylistId}", trackId, playlistId);
    }
    
    /// <summary>
    /// Gets the playlist ID for a given track ID from cache.
    /// Returns null if not found or expired.
    /// </summary>
    public string? GetPlaylistIdForTrack(string trackId)
    {
        if (_trackPlaylistCache.TryGetValue(trackId, out var entry))
        {
            if (entry.ExpiresAt > DateTime.UtcNow)
            {
                return entry.PlaylistId;
            }
            
            // Expired, remove it
            _trackPlaylistCache.TryRemove(trackId, out _);
        }
        
        return null;
    }
    
    /// <summary>
    /// Downloads all tracks from a playlist and creates an M3U file.
    /// This is triggered when a user stars a playlist.
    /// </summary>
    public async Task DownloadFullPlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting download for playlist {PlaylistId}", playlistId);
            
            // Parse playlist ID
            if (!PlaylistIdHelper.IsExternalPlaylist(playlistId))
            {
                _logger.LogWarning("Invalid playlist ID format: {PlaylistId}", playlistId);
                return;
            }
            
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
            
            // Get playlist metadata
            var metadataService = GetMetadataServiceForProvider(provider);
            if (metadataService == null)
            {
                throw new NotSupportedException($"Provider '{provider}' not supported for playlists");
            }
            
            var playlist = await metadataService.GetPlaylistAsync(provider, externalId);
            if (playlist == null)
            {
                _logger.LogWarning("Playlist not found: {PlaylistId}", playlistId);
                return;
            }
            
            var tracks = await metadataService.GetPlaylistTracksAsync(provider, externalId);
            if (tracks == null || tracks.Count == 0)
            {
                _logger.LogWarning("No tracks found in playlist {PlaylistId}", playlistId);
                return;
            }
            
            _logger.LogInformation("Found {TrackCount} tracks in playlist '{PlaylistName}'", tracks.Count, playlist.Name);
            
            // Get the appropriate download service for this provider
            var downloadService = _downloadServices.FirstOrDefault(s => 
                s.GetType().Name.Contains(provider, StringComparison.OrdinalIgnoreCase));
            
            if (downloadService == null)
            {
                _logger.LogError("No download service found for provider '{Provider}'", provider);
                return;
            }
            
            // Download all tracks (M3U will be created once at the end)
            var downloadedTracks = new List<(Song Song, string LocalPath)>();
            
            foreach (var track in tracks)
            {
                try
                {
                    if (string.IsNullOrEmpty(track.ExternalId))
                    {
                        _logger.LogWarning("Track has no external ID, skipping: {Title}", track.Title);
                        continue;
                    }
                    
                    // Add track to playlist cache BEFORE downloading
                    // This marks it as part of a full playlist download, so AddTrackToM3UAsync will skip real-time updates
                    var trackId = $"ext-{provider}-{track.ExternalId}";
                    AddTrackToPlaylistCache(trackId, playlistId);
                    
                    _logger.LogInformation("Downloading track '{Artist} - {Title}'", track.Artist, track.Title);
                    var localPath = await downloadService.DownloadSongAsync(provider, track.ExternalId, cancellationToken);
                    
                    downloadedTracks.Add((track, localPath));
                    _logger.LogDebug("Downloaded: {Path}", localPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download track '{Artist} - {Title}'", track.Artist, track.Title);
                }
            }
            
            if (downloadedTracks.Count == 0)
            {
                _logger.LogWarning("No tracks were successfully downloaded for playlist '{PlaylistName}'", playlist.Name);
                return;
            }
            
            // Create M3U file ONCE at the end with all downloaded tracks
            await CreateM3UPlaylistAsync(playlist.Name, downloadedTracks);
            
            _logger.LogInformation("Playlist download completed: {DownloadedCount}/{TotalCount} tracks for '{PlaylistName}'",
                downloadedTracks.Count, tracks.Count, playlist.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download playlist {PlaylistId}", playlistId);
            throw;
        }
    }
    
    /// <summary>
    /// Creates an M3U playlist file with relative paths to downloaded tracks
    /// </summary>
    private async Task CreateM3UPlaylistAsync(string playlistName, List<(Song Song, string LocalPath)> tracks)
    {
        try
        {
            // Sanitize playlist name for file system
            var fileName = PathHelper.SanitizeFileName(playlistName) + ".m3u";
            var playlistPath = Path.Combine(_playlistDirectory, fileName);
            
            var m3uContent = new StringBuilder();
            m3uContent.AppendLine("#EXTM3U");
            
            foreach (var (song, localPath) in tracks)
            {
                // Calculate relative path from playlist directory to track
                var relativePath = Path.GetRelativePath(_playlistDirectory, localPath);
                
                // Convert backslashes to forward slashes for M3U compatibility
                relativePath = relativePath.Replace('\\', '/');
                
                // Add EXTINF line with duration and artist - title
                var duration = song.Duration ?? 0;
                m3uContent.AppendLine($"#EXTINF:{duration},{song.Artist} - {song.Title}");
                m3uContent.AppendLine(relativePath);
            }
            
            await IOFile.WriteAllTextAsync(playlistPath, m3uContent.ToString());
            _logger.LogInformation("Created M3U playlist: {Path}", playlistPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create M3U playlist for '{PlaylistName}'", playlistName);
            throw;
        }
    }
    
    /// <summary>
    /// Adds a track to an existing M3U playlist or creates it if it doesn't exist.
    /// Called when individual tracks are played/downloaded (NOT during full playlist download).
    /// The M3U is rebuilt in the correct playlist order each time.
    /// </summary>
    /// <param name="isFullPlaylistDownload">If true, skips M3U update (will be done at the end by DownloadFullPlaylistAsync)</param>
    public async Task AddTrackToM3UAsync(string playlistId, Song track, string localPath, bool isFullPlaylistDownload = false)
    {
        // Skip real-time updates during full playlist download (M3U will be created once at the end)
        if (isFullPlaylistDownload)
        {
            _logger.LogDebug("Skipping M3U update for track {TrackId} (full playlist download in progress)", track.Id);
            return;
        }
        
        try
        {
            // Get playlist metadata to get the name and track order
            if (!PlaylistIdHelper.IsExternalPlaylist(playlistId))
            {
                _logger.LogWarning("Invalid playlist ID format: {PlaylistId}", playlistId);
                return;
            }
            
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
            
            var metadataService = GetMetadataServiceForProvider(provider);
            if (metadataService == null)
            {
                _logger.LogWarning("No metadata service found for provider '{Provider}'", provider);
                return;
            }
            
            var playlist = await metadataService.GetPlaylistAsync(provider, externalId);
            if (playlist == null)
            {
                _logger.LogWarning("Playlist not found: {PlaylistId}", playlistId);
                return;
            }
            
            // Get all tracks from the playlist to maintain order
            var allPlaylistTracks = await metadataService.GetPlaylistTracksAsync(provider, externalId);
            if (allPlaylistTracks == null || allPlaylistTracks.Count == 0)
            {
                _logger.LogWarning("No tracks found in playlist: {PlaylistId}", playlistId);
                return;
            }
            
            // Sanitize playlist name for file system
            var fileName = PathHelper.SanitizeFileName(playlist.Name) + ".m3u";
            var playlistPath = Path.Combine(_playlistDirectory, fileName);
            
            // Build M3U content in the correct order
            var m3uContent = new StringBuilder();
            m3uContent.AppendLine("#EXTM3U");
            
            int addedCount = 0;
            foreach (var playlistTrack in allPlaylistTracks)
            {
                // Check if this track has been downloaded locally
                string? trackLocalPath = null;
                
                // If this is the track we just downloaded
                if (playlistTrack.Id == track.Id)
                {
                    trackLocalPath = localPath;
                }
                else
                {
                    // Check if track was previously downloaded
                    var trackProvider = playlistTrack.ExternalProvider;
                    var trackExternalId = playlistTrack.ExternalId;
                    
                    if (!string.IsNullOrEmpty(trackProvider) && !string.IsNullOrEmpty(trackExternalId))
                    {
                        // Try to find the download service for this provider
                        var downloadService = _downloadServices.FirstOrDefault(s => 
                            s.GetType().Name.Contains(trackProvider, StringComparison.OrdinalIgnoreCase));
                        
                        if (downloadService != null)
                        {
                            trackLocalPath = await downloadService.GetLocalPathIfExistsAsync(trackProvider, trackExternalId);
                        }
                    }
                }
                
                // If track is downloaded, add it to M3U
                if (!string.IsNullOrEmpty(trackLocalPath) && IOFile.Exists(trackLocalPath))
                {
                    var relativePath = Path.GetRelativePath(_playlistDirectory, trackLocalPath);
                    relativePath = relativePath.Replace('\\', '/');
                    
                    var duration = playlistTrack.Duration ?? 0;
                    m3uContent.AppendLine($"#EXTINF:{duration},{playlistTrack.Artist} - {playlistTrack.Title}");
                    m3uContent.AppendLine(relativePath);
                    addedCount++;
                }
            }
            
            // Write the M3U file (overwrites existing)
            await IOFile.WriteAllTextAsync(playlistPath, m3uContent.ToString());
            _logger.LogInformation("Updated M3U playlist '{PlaylistName}' with {Count} tracks (in correct order)", 
                playlist.Name, addedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add track to M3U playlist");
        }
    }
    
    /// <summary>
    /// Background task to clean up expired cache entries every minute
    /// </summary>
    private async Task CleanupExpiredCacheEntriesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                
                var now = DateTime.UtcNow;
                var expiredKeys = _trackPlaylistCache
                    .Where(kvp => kvp.Value.ExpiresAt <= now)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var key in expiredKeys)
                {
                    _trackPlaylistCache.TryRemove(key, out _);
                }
                
                if (expiredKeys.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} expired playlist cache entries", expiredKeys.Count);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during playlist cache cleanup");
            }
        }
        
        _logger.LogInformation("Playlist cache cleanup task stopped");
    }
    
    /// <summary>
    /// Stops the background cleanup task
    /// </summary>
    public async Task StopCleanupAsync()
    {
        _cleanupCancellationTokenSource.Cancel();
        await _cleanupTask;
        _cleanupCancellationTokenSource.Dispose();
    }
}
