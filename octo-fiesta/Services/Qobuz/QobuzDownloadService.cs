using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Common;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.Qobuz;

/// <summary>
/// Download service implementation for Qobuz
/// Handles track downloading with MD5 signature for authentication
/// </summary>
public class QobuzDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly QobuzBundleService _bundleService;
    private readonly string? _userAuthToken;
    private readonly string? _userId;
    private readonly string? _preferredQuality;
    
    private const string BaseUrl = "https://www.qobuz.com/api.json/0.2/";
    
    // Quality format IDs
    private const int FormatMp3320 = 5;
    private const int FormatFlac16 = 6;      // CD quality (16-bit 44.1kHz)
    private const int FormatFlac24Low = 7;   // 24-bit < 96kHz
    private const int FormatFlac24High = 27; // 24-bit >= 96kHz

    protected override string ProviderName => "qobuz";

    public QobuzDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        QobuzBundleService bundleService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<QobuzSettings> qobuzSettings,
        ILogger<QobuzDownloadService> logger)
        : base(configuration, localLibraryService, metadataService, subsonicSettings.Value, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _bundleService = bundleService;
        
        var qobuzConfig = qobuzSettings.Value;
        _userAuthToken = qobuzConfig.UserAuthToken;
        _userId = qobuzConfig.UserId;
        _preferredQuality = qobuzConfig.Quality;
    }

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrEmpty(_userAuthToken) || string.IsNullOrEmpty(_userId))
        {
            Logger.LogWarning("Qobuz user auth token or user ID not configured");
            return false;
        }

        try
        {
            await _bundleService.GetAppIdAsync();
            await _bundleService.GetSecretsAsync();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Qobuz service not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-qobuz-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }

    protected override async Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        // Get the download URL with signature
        var downloadInfo = await GetTrackDownloadUrlAsync(trackId, cancellationToken);
        
        Logger.LogInformation("Download URL obtained for: {Title} - {Artist}", song.Title, song.Artist);
        Logger.LogInformation("Quality: {BitDepth}bit/{SamplingRate}kHz, Format: {MimeType}", 
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
        var basePath = SubsonicSettings.StorageMode == StorageMode.Cache ? CachePath : DownloadPath;
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension);
        
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

    #endregion

    #region Qobuz Download Methods

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
                        Logger.LogWarning("Requested quality not available, Qobuz downgraded to {BitDepth}bit/{SamplingRate}kHz",
                            result.BitDepth, result.SamplingRate);
                    }
                    
                    return result;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Logger.LogDebug("Failed to get download URL with secret {SecretIndex}, format {Format}: {Error}", 
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
        
        var url = $"{BaseUrl}track/getFileUrl?format_id={formatId}&intent=stream&request_ts={unix}&track_id={trackId}&request_sig={signature}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
        request.Headers.Add("X-App-Id", appId);
        
        if (!string.IsNullOrEmpty(_userAuthToken))
        {
            request.Headers.Add("X-User-Auth-Token", _userAuthToken);
        }
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogDebug("Qobuz getFileUrl failed - Status: {StatusCode}, TrackId: {TrackId}, FormatId: {FormatId}", 
                response.StatusCode, trackId, formatId);
            throw new HttpRequestException($"Response status code does not indicate success: {response.StatusCode} ({response.ReasonPhrase})");
        }
        
        var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("url", out var urlElement) || string.IsNullOrEmpty(urlElement.GetString()))
        {
            throw new Exception("No download URL in response");
        }
        
        var downloadUrl = urlElement.GetString()!;
        var mimeType = root.TryGetProperty("mime_type", out var mime) ? mime.GetString() : null;
        var bitDepth = root.TryGetProperty("bit_depth", out var bd) ? bd.GetInt32() : 16;
        var samplingRate = root.TryGetProperty("sampling_rate", out var sr) ? sr.GetDouble() : 44.1;
        
        var isSample = root.TryGetProperty("sample", out var sampleEl) && sampleEl.GetBoolean();
        if (samplingRate == 0)
        {
            isSample = true;
        }
        
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
    /// </summary>
    private string ComputeMD5Signature(string trackId, int formatId, long timestamp, string secret)
    {
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
            return FormatFlac24High;
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
    /// Gets the list of format IDs to try in priority order
    /// </summary>
    private List<int> GetFormatPriority(int preferredFormat)
    {
        var allFormats = new List<int> { FormatFlac24High, FormatFlac24Low, FormatFlac16, FormatMp3320 };
        
        var priority = new List<int> { preferredFormat };
        priority.AddRange(allFormats.Where(f => f != preferredFormat));
        
        return priority;
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
