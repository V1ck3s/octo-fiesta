using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using octo_fiesta.Models;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services;

/// <summary>
/// Download service implementation for Qobuz
/// Handles track downloading with MD5 signature for authentication
/// </summary>
public class QobuzDownloadService : IDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IMusicMetadataService _metadataService;
    private readonly QobuzBundleService _bundleService;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<QobuzDownloadService> _logger;
    
    private readonly string _downloadPath;
    private readonly string? _userAuthToken;
    private readonly string? _userId;
    private readonly string? _preferredQuality;
    
    private readonly Dictionary<string, DownloadInfo> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    
    private const string BaseUrl = "https://www.qobuz.com/api.json/0.2/";
    
    // Quality format IDs
    private const int FormatMp3320 = 5;
    private const int FormatFlac16 = 6;      // CD quality (16-bit 44.1kHz)
    private const int FormatFlac24Low = 7;   // 24-bit < 96kHz
    private const int FormatFlac24High = 27; // 24-bit >= 96kHz

    public QobuzDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        QobuzBundleService bundleService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<QobuzSettings> qobuzSettings,
        ILogger<QobuzDownloadService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _configuration = configuration;
        _localLibraryService = localLibraryService;
        _metadataService = metadataService;
        _bundleService = bundleService;
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;
        
        _downloadPath = configuration["Library:DownloadPath"] ?? "./downloads";
        
        var qobuzConfig = qobuzSettings.Value;
        _userAuthToken = qobuzConfig.UserAuthToken;
        _userId = qobuzConfig.UserId;
        _preferredQuality = qobuzConfig.Quality;
        
        if (!Directory.Exists(_downloadPath))
        {
            Directory.CreateDirectory(_downloadPath);
        }
    }

    #region IDownloadService Implementation

    public async Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, cancellationToken);
    }

    /// <summary>
    /// Internal method for downloading a song with control over album download triggering
    /// </summary>
    private async Task<string> DownloadSongInternalAsync(string externalProvider, string externalId, bool triggerAlbumDownload, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "qobuz")
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        var songId = $"ext-{externalProvider}-{externalId}";
        
        // Check if already downloaded
        var existingPath = await _localLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (existingPath != null && IOFile.Exists(existingPath))
        {
            _logger.LogInformation("Song already downloaded: {Path}", existingPath);
            return existingPath;
        }

        // Check if download in progress
        if (_activeDownloads.TryGetValue(songId, out var activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
        {
            _logger.LogInformation("Download already in progress for {SongId}", songId);
            while (_activeDownloads.TryGetValue(songId, out activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
            {
                await Task.Delay(500, cancellationToken);
            }
            
            if (activeDownload?.Status == DownloadStatus.Completed && activeDownload.LocalPath != null)
            {
                return activeDownload.LocalPath;
            }
            
            throw new Exception(activeDownload?.ErrorMessage ?? "Download failed");
        }

        await _downloadLock.WaitAsync(cancellationToken);
        try
        {
            // Get metadata
            var song = await _metadataService.GetSongAsync(externalProvider, externalId);
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
            _activeDownloads[songId] = downloadInfo;

            try
            {
                var localPath = await DownloadTrackAsync(externalId, song, cancellationToken);
                
                downloadInfo.Status = DownloadStatus.Completed;
                downloadInfo.LocalPath = localPath;
                downloadInfo.CompletedAt = DateTime.UtcNow;
                
                song.LocalPath = localPath;
                await _localLibraryService.RegisterDownloadedSongAsync(song, localPath);
                
                // Trigger a Subsonic library rescan (with debounce)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _localLibraryService.TriggerLibraryScanAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to trigger library scan after download");
                    }
                });
                
                // If download mode is Album and triggering is enabled, start background download of remaining tracks
                if (triggerAlbumDownload && _subsonicSettings.DownloadMode == DownloadMode.Album && !string.IsNullOrEmpty(song.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(song.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        _logger.LogInformation("Download mode is Album, triggering background download for album {AlbumId}", albumExternalId);
                        DownloadRemainingAlbumTracksInBackground(externalProvider, albumExternalId, externalId);
                    }
                }
                
                _logger.LogInformation("Download completed: {Path}", localPath);
                return localPath;
            }
            catch (Exception ex)
            {
                downloadInfo.Status = DownloadStatus.Failed;
                downloadInfo.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Download failed for {SongId}", songId);
                throw;
            }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    public async Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var localPath = await DownloadSongAsync(externalProvider, externalId, cancellationToken);
        return IOFile.OpenRead(localPath);
    }

    public DownloadInfo? GetDownloadStatus(string songId)
    {
        _activeDownloads.TryGetValue(songId, out var info);
        return info;
    }

    public async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrEmpty(_userAuthToken) || string.IsNullOrEmpty(_userId))
        {
            _logger.LogWarning("Qobuz user auth token or user ID not configured");
            return false;
        }

        try
        {
            // Try to extract app ID and secrets
            await _bundleService.GetAppIdAsync();
            await _bundleService.GetSecretsAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qobuz service not available");
            return false;
        }
    }

    public void DownloadRemainingAlbumTracksInBackground(string externalProvider, string albumExternalId, string excludeTrackExternalId)
    {
        if (externalProvider != "qobuz")
        {
            _logger.LogWarning("Provider '{Provider}' is not supported for album download", externalProvider);
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
                _logger.LogError(ex, "Failed to download remaining album tracks for album {AlbumId}", albumExternalId);
            }
        });
    }

    private async Task DownloadRemainingAlbumTracksAsync(string albumExternalId, string excludeTrackExternalId)
    {
        _logger.LogInformation("Starting background download for album {AlbumId} (excluding track {TrackId})", 
            albumExternalId, excludeTrackExternalId);

        var album = await _metadataService.GetAlbumAsync("qobuz", albumExternalId);
        if (album == null)
        {
            _logger.LogWarning("Album {AlbumId} not found, cannot download remaining tracks", albumExternalId);
            return;
        }

        var tracksToDownload = album.Songs
            .Where(s => s.ExternalId != excludeTrackExternalId && !string.IsNullOrEmpty(s.ExternalId))
            .ToList();

        _logger.LogInformation("Found {Count} additional tracks to download for album '{AlbumTitle}'", 
            tracksToDownload.Count, album.Title);

        foreach (var track in tracksToDownload)
        {
            try
            {
                var existingPath = await _localLibraryService.GetLocalPathForExternalSongAsync("qobuz", track.ExternalId!);
                if (existingPath != null && IOFile.Exists(existingPath))
                {
                    _logger.LogDebug("Track {TrackId} already downloaded, skipping", track.ExternalId);
                    continue;
                }

                _logger.LogInformation("Downloading track '{Title}' from album '{Album}'", track.Title, album.Title);
                await DownloadSongInternalAsync("qobuz", track.ExternalId!, triggerAlbumDownload: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download track {TrackId} '{Title}'", track.ExternalId, track.Title);
            }
        }

        _logger.LogInformation("Completed background download for album '{AlbumTitle}'", album.Title);
    }

    #endregion

    #region Qobuz Download Methods

    private async Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        // Get the download URL with signature
        var downloadInfo = await GetTrackDownloadUrlAsync(trackId, cancellationToken);
        
        _logger.LogInformation("Download URL obtained for: {Title} - {Artist}", song.Title, song.Artist);
        _logger.LogInformation("Quality: {BitDepth}bit/{SamplingRate}kHz, Format: {MimeType}", 
            downloadInfo.BitDepth, downloadInfo.SamplingRate, downloadInfo.MimeType);

        // Check if it's a demo/sample
        if (downloadInfo.IsSample)
        {
            throw new Exception("Track is only available as a demo/sample");
        }

        // Determine extension based on MIME type
        var extension = downloadInfo.MimeType?.Contains("flac") == true ? ".flac" : ".mp3";

        // Build organized folder structure using AlbumArtist (fallback to Artist for singles)
        var artistForPath = song.AlbumArtist ?? song.Artist;
        var outputPath = PathHelper.BuildTrackPath(_downloadPath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        outputPath = PathHelper.ResolveUniquePath(outputPath);

        // Download the file (Qobuz files are NOT encrypted like Deezer)
        var response = await _httpClient.GetAsync(downloadInfo.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = IOFile.Create(outputPath);
        
        await responseStream.CopyToAsync(outputFile, cancellationToken);
        await outputFile.DisposeAsync();
        
        // Write metadata and cover art
        await WriteMetadataAsync(outputPath, song, cancellationToken);

        return outputPath;
    }

    /// <summary>
    /// Gets the download URL for a track with proper MD5 signature
    /// </summary>
    private async Task<QobuzDownloadResult> GetTrackDownloadUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        var appId = await _bundleService.GetAppIdAsync();
        var secrets = await _bundleService.GetSecretsAsync();
        
        if (secrets.Count == 0)
        {
            throw new Exception("No secrets available for signing");
        }
        
        // Determine format ID based on preferred quality
        var formatId = GetFormatId(_preferredQuality);
        
        // Try the preferred quality first, then fallback to lower qualities
        var formatPriority = GetFormatPriority(formatId);
        
        Exception? lastException = null;
        
        // Try each secret with each format
        foreach (var secret in secrets)
        {
            var secretIndex = secrets.IndexOf(secret);
            foreach (var format in formatPriority)
            {
                try
                {
                    var result = await TryGetTrackDownloadUrlAsync(trackId, format, secret, cancellationToken);
                    
                    // Check if quality was downgraded
                    if (result.WasQualityDowngraded)
                    {
                        _logger.LogWarning("Requested quality not available, Qobuz downgraded to {BitDepth}bit/{SamplingRate}kHz",
                            result.BitDepth, result.SamplingRate);
                    }
                    
                    return result;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogDebug("Failed to get download URL with secret {SecretIndex}, format {Format}: {Error}", 
                        secretIndex, format, ex.Message);
                }
            }
        }
        
        throw new Exception($"Failed to get download URL for all secrets and quality formats", lastException);
    }

    private async Task<QobuzDownloadResult> TryGetTrackDownloadUrlAsync(string trackId, int formatId, string secret, CancellationToken cancellationToken)
    {
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var appId = await _bundleService.GetAppIdAsync();
        var signature = ComputeMD5Signature(trackId, formatId, unix, secret);
        
        // Build URL with required parameters (app_id goes in header only, not in URL params)
        var url = $"{BaseUrl}track/getFileUrl?format_id={formatId}&intent=stream&request_ts={unix}&track_id={trackId}&request_sig={signature}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        // Add required headers (matching qobuz-dl Python implementation)
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
        request.Headers.Add("X-App-Id", appId);
        
        if (!string.IsNullOrEmpty(_userAuthToken))
        {
            request.Headers.Add("X-User-Auth-Token", _userAuthToken);
        }
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        
        // Read response body
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        
        // Log error response if not successful
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Qobuz getFileUrl failed - Status: {StatusCode}, TrackId: {TrackId}, FormatId: {FormatId}", 
                response.StatusCode, trackId, formatId);
            throw new HttpRequestException($"Response status code does not indicate success: {response.StatusCode} ({response.ReasonPhrase})");
        }
        
        var json = responseBody;
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("url", out var urlElement) || string.IsNullOrEmpty(urlElement.GetString()))
        {
            throw new Exception("No download URL in response");
        }
        
        var downloadUrl = urlElement.GetString()!;
        var mimeType = root.TryGetProperty("mime_type", out var mime) ? mime.GetString() : null;
        var bitDepth = root.TryGetProperty("bit_depth", out var bd) ? bd.GetInt32() : 16;
        var samplingRate = root.TryGetProperty("sampling_rate", out var sr) ? sr.GetDouble() : 44.1;
        
        // Check if it's a sample/demo
        var isSample = root.TryGetProperty("sample", out var sampleEl) && sampleEl.GetBoolean();
        
        // If sampling_rate is null/0, it's likely a demo
        if (samplingRate == 0)
        {
            isSample = true;
        }
        
        // Check for quality restrictions/downgrades
        var wasDowngraded = false;
        if (root.TryGetProperty("restrictions", out var restrictions))
        {
            foreach (var restriction in restrictions.EnumerateArray())
            {
                if (restriction.TryGetProperty("code", out var code))
                {
                    var codeStr = code.GetString();
                    if (codeStr == "FormatRestrictedByFormatAvailability")
                    {
                        wasDowngraded = true;
                    }
                }
            }
        }
        
        return new QobuzDownloadResult
        {
            Url = downloadUrl,
            FormatId = formatId,
            MimeType = mimeType,
            BitDepth = bitDepth,
            SamplingRate = samplingRate,
            IsSample = isSample,
            WasQualityDowngraded = wasDowngraded
        };
    }

    /// <summary>
    /// Computes MD5 signature for track download request
    /// Format based on qobuz-dl: trackgetFileUrlformat_id{X}intentstreamtrack_id{Y}{TIMESTAMP}{SECRET}
    /// </summary>
    private string ComputeMD5Signature(string trackId, int formatId, long timestamp, string secret)
    {
        // EXACT format from qobuz-dl Python implementation:
        // "trackgetFileUrlformat_id{}intentstreamtrack_id{}{}{}".format(fmt_id, track_id, unix, secret)
        var toSign = $"trackgetFileUrlformat_id{formatId}intentstreamtrack_id{trackId}{timestamp}{secret}";
        
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(toSign));
        var signature = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        
        return signature;
    }

    /// <summary>
    /// Gets the format ID based on quality preference
    /// </summary>
    private int GetFormatId(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
        {
            return FormatFlac24High; // Default to highest quality
        }
        
        return quality.ToUpperInvariant() switch
        {
            "FLAC" => FormatFlac24High,
            "FLAC_24_HIGH" or "24_192" => FormatFlac24High,
            "FLAC_24_LOW" or "24_96" => FormatFlac24Low,
            "FLAC_16" or "CD" => FormatFlac16,
            "MP3_320" or "MP3" => FormatMp3320,
            _ => FormatFlac24High
        };
    }

    /// <summary>
    /// Gets the list of format IDs to try in priority order (highest to lowest)
    /// </summary>
    private List<int> GetFormatPriority(int preferredFormat)
    {
        var allFormats = new List<int> { FormatFlac24High, FormatFlac24Low, FormatFlac16, FormatMp3320 };
        
        // Start with preferred format, then try others in descending quality order
        var priority = new List<int> { preferredFormat };
        priority.AddRange(allFormats.Where(f => f != preferredFormat));
        
        return priority;
    }

    /// <summary>
    /// Writes ID3/Vorbis metadata and cover art to the audio file
    /// </summary>
    private async Task WriteMetadataAsync(string filePath, Song song, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Writing metadata to: {Path}", filePath);
            
            using var tagFile = TagLib.File.Create(filePath);
            
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
                        _logger.LogInformation("Cover art embedded: {Size} bytes", coverData.Length);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download cover art from {Url}", coverUrl);
                }
            }
            
            tagFile.Save();
            _logger.LogInformation("Metadata written successfully to: {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write metadata to: {Path}", filePath);
        }
    }

    private async Task<byte[]?> DownloadCoverArtAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download cover art from {Url}", url);
            return null;
        }
    }

    #endregion

    #region Utility Methods

    private static string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-qobuz-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }

    private void EnsureDirectoryExists(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _logger.LogDebug("Created directory: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create directory: {Path}", path);
            throw;
        }
    }

    #endregion

    private class QobuzDownloadResult
    {
        public string Url { get; set; } = string.Empty;
        public int FormatId { get; set; }
        public string? MimeType { get; set; }
        public int BitDepth { get; set; }
        public double SamplingRate { get; set; }
        public bool IsSample { get; set; }
        public bool WasQualityDowngraded { get; set; }
    }
}
