using System.Text;
using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.SquidWTF;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Common;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Download service implementation using SquidWTF API
/// Supports both Qobuz and Tidal backends
/// No decryption needed - SquidWTF returns direct streaming URLs
/// </summary>
public class SquidWTFDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly SquidWTFSettings _squidWTFSettings;
    
    // API endpoints
    private const string QobuzBaseUrl = "https://qobuz.squid.wtf";
    private const string TidalBaseUrl = "https://triton.squid.wtf";
    
    // Required headers
    private const string QobuzCountryHeader = "Token-Country";
    private const string QobuzCountryValue = "US";
    private const string TidalClientHeader = "x-client";
    private const string TidalClientValue = "BiniLossless/v3.4";
    
    // Quality mappings
    // Qobuz: 27 = FLAC (24-bit), 7 = FLAC (16-bit), 6 = MP3 320, 5 = MP3 128
    // Tidal: HI_RES_LOSSLESS, LOSSLESS
    
    private bool IsQobuzSource => _squidWTFSettings.Source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase);

    protected override string ProviderName => "squidwtf";

    public SquidWTFDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<SquidWTFSettings> squidWTFSettings,
        IServiceProvider serviceProvider,
        ILogger<SquidWTFDownloadService> logger)
        : base(configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _squidWTFSettings = squidWTFSettings.Value;
    }

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        try
        {
            // Test connectivity to the appropriate backend
            if (IsQobuzSource)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{QobuzBaseUrl}/api/get-music?q=test&offset=0");
                request.Headers.Add(QobuzCountryHeader, QobuzCountryValue);
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{TidalBaseUrl}/search/?s=test");
                request.Headers.Add(TidalClientHeader, TidalClientValue);
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SquidWTF service not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-squidwtf-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }

    protected override string? GetTargetQuality()
    {
        if (!string.IsNullOrEmpty(_squidWTFSettings.Quality))
        {
            return _squidWTFSettings.Quality;
        }
        
        // Default to highest quality
        return IsQobuzSource ? "27" : "HI_RES_LOSSLESS";
    }

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        if (IsQobuzSource)
        {
            return await DownloadTrackQobuzAsync(trackId, song, cancellationToken);
        }
        else
        {
            return await DownloadTrackTidalAsync(trackId, song, cancellationToken);
        }
    }

    #endregion

    #region Qobuz Download

    private async Task<DownloadResult> DownloadTrackQobuzAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        // Get download URL
        var quality = GetQobuzQuality();
        var url = $"{QobuzBaseUrl}/api/download-music?track_id={trackId}&quality={quality}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(QobuzCountryHeader, QobuzCountryValue);
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var downloadResponse = JsonSerializer.Deserialize<QobuzDownloadResponse>(json);
        
        if (downloadResponse?.Success != true || string.IsNullOrEmpty(downloadResponse.Data?.Url))
        {
            throw new Exception("Failed to get download URL from SquidWTF Qobuz");
        }
        
        var downloadUrl = downloadResponse.Data.Url;
        Logger.LogInformation("Got download URL for track {TrackId}: {Title}", trackId, song.Title);
        
        // Determine file extension based on quality
        var extension = quality == "27" || quality == "7" ? ".flac" : ".mp3";
        var downloadedQuality = quality switch
        {
            "27" => "FLAC_24",
            "7" => "FLAC_16",
            "6" => "MP3_320",
            "5" => "MP3_128",
            _ => "FLAC"
        };
        
        // Build output path
        var artistForPath = song.AlbumArtist ?? song.Artist;
        var basePath = SubsonicSettings.StorageMode == StorageMode.Cache ? CachePath : DownloadPath;
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        // Create directories
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);
        
        // Download the file (no decryption needed)
        await DownloadFileAsync(downloadUrl, outputPath, cancellationToken);
        
        // Write metadata
        await WriteMetadataAsync(outputPath, song, cancellationToken);
        
        return new DownloadResult(outputPath, downloadedQuality);
    }

    private string GetQobuzQuality()
    {
        var quality = _squidWTFSettings.Quality;
        
        if (string.IsNullOrEmpty(quality))
        {
            return "27"; // Default to highest quality FLAC
        }
        
        // Map common quality names to Qobuz quality codes
        return quality.ToUpperInvariant() switch
        {
            "FLAC" or "FLAC_24" or "27" => "27",
            "FLAC_16" or "7" => "7",
            "MP3_320" or "6" => "6",
            "MP3_128" or "5" => "5",
            _ => "27"
        };
    }

    #endregion

    #region Tidal Download

    private async Task<DownloadResult> DownloadTrackTidalAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var requestedQuality = GetTidalQuality();
        var (manifest, actualQuality) = await GetTidalManifestAsync(trackId, requestedQuality, cancellationToken);
        
        if (manifest?.Urls == null || manifest.Urls.Count == 0)
        {
            throw new Exception("No download URLs in Tidal manifest");
        }
        
        var downloadUrl = manifest.Urls[0];
        Logger.LogInformation("Got download URL for track {TrackId}: {Title} (quality: {Quality})", trackId, song.Title, actualQuality);
        
        // Determine file extension based on manifest mime type
        var extension = GetExtensionFromMimeType(manifest.MimeType);
        var downloadedQuality = GetDownloadedQuality(actualQuality, manifest.MimeType);
        
        // Build output path
        var artistForPath = song.AlbumArtist ?? song.Artist;
        var basePath = SubsonicSettings.StorageMode == StorageMode.Cache ? CachePath : DownloadPath;
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        // Create directories
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);
        
        // Download the file (no decryption needed)
        await DownloadFileAsync(downloadUrl, outputPath, cancellationToken);
        
        // Write metadata
        await WriteMetadataAsync(outputPath, song, cancellationToken);
        
        return new DownloadResult(outputPath, downloadedQuality);
    }

    /// <summary>
    /// Gets the Tidal manifest, falling back to LOSSLESS if HI_RES_LOSSLESS returns DASH format
    /// </summary>
    private async Task<(TidalManifest? manifest, string quality)> GetTidalManifestAsync(
        string trackId, string quality, CancellationToken cancellationToken)
    {
        var url = $"{TidalBaseUrl}/track/?id={trackId}&quality={quality}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TidalClientHeader, TidalClientValue);
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var wrapper = JsonSerializer.Deserialize<TidalTrackDownloadResponseWrapper>(json);
        var trackResponse = wrapper?.Data;
        
        if (string.IsNullOrEmpty(trackResponse?.Manifest))
        {
            throw new Exception("Failed to get manifest from SquidWTF Tidal");
        }
        
        // Check if manifest is DASH (XML) format - not supported, need to fallback to LOSSLESS
        var manifestMimeType = trackResponse.ManifestMimeType ?? "";
        if (manifestMimeType.Contains("dash+xml") || manifestMimeType.Contains("application/dash"))
        {
            if (quality == "HI_RES_LOSSLESS")
            {
                Logger.LogWarning("HI_RES_LOSSLESS returned DASH format for track {TrackId}, falling back to LOSSLESS", trackId);
                return await GetTidalManifestAsync(trackId, "LOSSLESS", cancellationToken);
            }
            throw new Exception($"Unsupported manifest format: {manifestMimeType}");
        }
        
        // Decode the base64 manifest (JSON format)
        var manifestJson = Encoding.UTF8.GetString(Convert.FromBase64String(trackResponse.Manifest));
        var manifest = JsonSerializer.Deserialize<TidalManifest>(manifestJson);
        
        return (manifest, quality);
    }

    private string GetTidalQuality()
    {
        var quality = _squidWTFSettings.Quality;
        
        if (string.IsNullOrEmpty(quality))
        {
            return "HI_RES_LOSSLESS"; // Default to highest quality
        }
        
        // Map common quality names to Tidal quality codes
        return quality.ToUpperInvariant() switch
        {
            "HI_RES_LOSSLESS" or "HI_RES" or "FLAC_24" => "HI_RES_LOSSLESS",
            "LOSSLESS" or "FLAC" or "FLAC_16" => "LOSSLESS",
            _ => "HI_RES_LOSSLESS"
        };
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Determines file extension based on the manifest's mime type
    /// </summary>
    private static string GetExtensionFromMimeType(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType))
            return ".mp3";
            
        return mimeType.ToLowerInvariant() switch
        {
            var m when m.Contains("flac") => ".flac",
            var m when m.Contains("mp4") || m.Contains("m4a") || m.Contains("aac") => ".m4a",
            var m when m.Contains("mp3") || m.Contains("mpeg") => ".mp3",
            _ => ".mp3"
        };
    }

    /// <summary>
    /// Determines the quality string for the downloaded file
    /// </summary>
    private static string GetDownloadedQuality(string requestedQuality, string? mimeType)
    {
        if (mimeType?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true)
        {
            return requestedQuality == "HI_RES_LOSSLESS" ? "FLAC_24" : "FLAC_16";
        }
        
        // AAC/M4A from Tidal is typically 256kbps
        if (mimeType?.Contains("mp4", StringComparison.OrdinalIgnoreCase) == true ||
            mimeType?.Contains("aac", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "AAC_256";
        }
        
        return "MP3_320";
    }

    private async Task DownloadFileAsync(string url, string outputPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Add("Accept", "*/*");
        
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = IOFile.Create(outputPath);
        
        await responseStream.CopyToAsync(outputFile, cancellationToken);
        
        Logger.LogInformation("Downloaded file to: {Path}", outputPath);
    }

    #endregion
}
