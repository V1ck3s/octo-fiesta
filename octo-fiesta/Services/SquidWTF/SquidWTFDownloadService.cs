using System.Text;
using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.SquidWTF;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Download service implementation using SquidWTF API
/// Supports Qobuz and Tidal backends
/// No decryption needed - SquidWTF returns direct streaming URLs
/// </summary>
public class SquidWTFDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly SquidWTFSettings _squidWTFSettings;
    private readonly SquidWTFInstanceManager _instanceManager;
    private readonly SquidWTFCaptchaSolver _captchaSolver;
    
    // Static Qobuz API endpoint
    private const string QobuzBaseUrl = "https://qobuz.squid.wtf";

    // Required headers
    private const string QobuzCountryHeader = "Token-Country";
    private const string QobuzCountryValue = "US";
    private const string TidalClientHeader = "x-client";
    private const string TidalClientValue = "BiniLossless/v3.4";

    // Quality mappings
    // Qobuz: 27 = FLAC 24-bit/192kHz, 7 = FLAC 24-bit/96kHz, 6 = FLAC 16-bit/44kHz, 5 = MP3 320kbps
    // Tidal: HI_RES_LOSSLESS (FLAC 24-bit), LOSSLESS (FLAC 16-bit), HIGH (320kbps AAC), LOW (96kbps AAC)

    private bool IsQobuzSource => _squidWTFSettings.Source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase);

    protected override string ProviderName => "squidwtf";

    public SquidWTFDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<SquidWTFSettings> squidWTFSettings,
        SquidWTFInstanceManager instanceManager,
        SquidWTFCaptchaSolver captchaSolver,
        IServiceProvider serviceProvider,
        ILogger<SquidWTFDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _squidWTFSettings = squidWTFSettings.Value;
        _instanceManager = instanceManager;
        _captchaSolver = captchaSolver;
    }

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        try
        {
            if (IsQobuzSource)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{QobuzBaseUrl}/api/get-music?q=test&offset=0");
                request.Headers.Add(QobuzCountryHeader, QobuzCountryValue);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }

            // Tidal — test with instance manager
            {
                var response = await _instanceManager.SendWithFailoverAsync(baseUrl =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/search/?s=test");
                    request.Headers.Add(TidalClientHeader, TidalClientValue);
                    return request;
                });
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
            return _squidWTFSettings.Quality;

        if (IsQobuzSource) return "27";
        return "HI_RES_LOSSLESS";
    }

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        if (IsQobuzSource)
            return await DownloadTrackQobuzAsync(trackId, song, cancellationToken);
        return await DownloadTrackTidalAsync(trackId, song, cancellationToken);
    }

    #endregion

    #region Qobuz Download

    private async Task<DownloadResult> DownloadTrackQobuzAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var quality = GetQobuzQuality();
        var url = $"{QobuzBaseUrl}/api/download-music?track_id={trackId}&quality={quality}";

        var response = await SendQobuzDownloadRequestAsync(url, forceCaptchaRefresh: false, cancellationToken);

        // Cached captcha cookie may be stale (>30 min server-side); refresh on 403 and retry once.
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("Captcha required", StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                Logger.LogInformation("SquidWTF Qobuz captcha required, refreshing session and retrying");
                response = await SendQobuzDownloadRequestAsync(url, forceCaptchaRefresh: true, cancellationToken);
            }
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        var downloadResponse = JsonSerializer.Deserialize<QobuzDownloadResponse>(json);
        
        if (downloadResponse?.Success != true || string.IsNullOrEmpty(downloadResponse.Data?.Url))
        {
            throw new Exception("Failed to get download URL from SquidWTF Qobuz");
        }
        
        var downloadUrl = downloadResponse.Data.Url;
        Logger.LogInformation("Got download URL for track {TrackId}: {Title}", trackId, song.Title);

        Stream downloadStream = await GetDownloadStreamAsync(downloadUrl, cancellationToken);        
        // Determine file extension based on quality
        // Qobuz: 27/7/6 = FLAC, 5 = MP3
        var extension = quality == "5" ? ".mp3" : ".flac";
        var downloadedQuality = quality switch
        {
            "27" => "FLAC_24_192",
            "7" => "FLAC_24_96",
            "6" => "FLAC_16",
            "5" => "MP3_320",
            _ => "FLAC"
        };

        return new DownloadResult(downloadStream, extension, downloadedQuality);
    }

    private async Task<HttpResponseMessage> SendQobuzDownloadRequestAsync(string url, bool forceCaptchaRefresh, CancellationToken cancellationToken)
    {
        var cookie = await _captchaSolver.GetCaptchaCookieAsync(QobuzBaseUrl, forceCaptchaRefresh, cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(QobuzCountryHeader, QobuzCountryValue);
        request.Headers.Add("Cookie", cookie);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private string GetQobuzQuality()
    {
        var quality = _squidWTFSettings.Quality;
        
        if (string.IsNullOrEmpty(quality))
        {
            return "27"; // Default to highest quality FLAC (24-bit/192kHz)
        }
        
        // Map common quality names to Qobuz quality codes
        // 27 = FLAC 24-bit/192kHz, 7 = FLAC 24-bit/96kHz, 6 = FLAC 16-bit/44kHz, 5 = MP3 320kbps
        return quality.ToUpperInvariant() switch
        {
            "FLAC_24_192" or "FLAC_24" or "27" => "27",
            "FLAC_24_96" or "7" => "7",
            "FLAC_16" or "FLAC" or "6" => "6",
            "MP3_320" or "MP3" or "5" => "5",
            _ => "27"
        };
    }

    #endregion

    #region Tidal Download

    private async Task<DownloadResult> DownloadTrackTidalAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var requestedQuality = GetTidalQuality();
        var (manifest, actualQuality) = await GetTidalManifestAsync(trackId, requestedQuality, song.Duration, cancellationToken);

        if (manifest?.Urls == null || manifest.Urls.Count == 0)
        {
            throw new Exception("No download URLs in Tidal manifest");
        }

        Stream downloadStream;
        if (manifest.Urls.Count > 1)
        {
            Logger.LogInformation(
                "Downloading {SegmentCount} DASH segments for track {TrackId}: {Title} (quality: {Quality}, codecs: {Codecs})",
                manifest.Urls.Count, trackId, song.Title, actualQuality, manifest.Codecs ?? "?");
            downloadStream = new MultiSegmentHttpStream(_httpClient, manifest.Urls);
        }
        else
        {
            Logger.LogInformation(
                "Got download URL for track {TrackId}: {Title} (quality: {Quality})",
                trackId, song.Title, actualQuality);
            downloadStream = await GetDownloadStreamAsync(manifest.Urls[0], cancellationToken);
        }

        var extension = GetExtensionFromMimeType(manifest.MimeType, manifest.Codecs);
        var downloadedQuality = GetDownloadedQuality(actualQuality, manifest.MimeType, manifest.Codecs);

        // fMP4 (FLAC-in-MP4 DASH) assembles into a file whose moov duration is 0; pass the
        // known duration so it can be patched, otherwise scanners report 0:00. (see #251)
        var mp4Duration = extension == ".m4a"
            ? manifest.DurationSeconds ?? (double?)song.Duration
            : null;

        return new DownloadResult(downloadStream, extension, downloadedQuality, mp4Duration);
    }

    /// <summary>
    /// Gets the Tidal manifest. Handles both the legacy BTS JSON manifest and the
    /// DASH MPD manifest now served for HI_RES_LOSSLESS — DASH segments are flattened
    /// into the same <see cref="TidalManifest.Urls"/> list (init segment first).
    /// Uses the instance manager for automatic failover.
    /// </summary>
    internal async Task<(TidalManifest? manifest, string quality)> GetTidalManifestAsync(
        string trackId, string quality, int? expectedDurationSeconds, CancellationToken cancellationToken)
    {
        var response = await _instanceManager.SendWithFailoverAsync(baseUrl =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/track/?id={trackId}&quality={quality}");
            request.Headers.Add(TidalClientHeader, TidalClientValue);
            return request;
        }, cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var wrapper = JsonSerializer.Deserialize<TidalTrackDownloadResponseWrapper>(json);
        var trackResponse = wrapper?.Data;

        if (string.IsNullOrEmpty(trackResponse?.Manifest))
        {
            throw new Exception("Failed to get manifest from SquidWTF Tidal");
        }

        var manifestBytes = Convert.FromBase64String(trackResponse.Manifest);
        var manifestText = Encoding.UTF8.GetString(manifestBytes);
        var manifestMimeType = trackResponse.ManifestMimeType ?? "";

        TidalManifest? manifest;
        if (manifestMimeType.Contains("dash+xml") || manifestMimeType.Contains("application/dash"))
        {
            try
            {
                var parsed = TidalDashManifestParser.Parse(manifestText);
                Logger.LogInformation(
                    "Parsed DASH manifest for track {TrackId}: {SegmentCount} segments, codecs={Codecs}",
                    trackId, parsed.Urls.Count, parsed.Codecs);

                manifest = new TidalManifest
                {
                    MimeType = parsed.MimeType ?? "audio/mp4",
                    Codecs = parsed.Codecs,
                    Urls = parsed.Urls.ToList(),
                    DurationSeconds = parsed.DurationSeconds,
                };
            }
            catch (Exception ex) when (quality == "HI_RES_LOSSLESS")
            {
                Logger.LogWarning(ex,
                    "Failed to parse HI_RES_LOSSLESS DASH manifest for track {TrackId}, falling back to LOSSLESS",
                    trackId);
                return await GetTidalManifestAsync(trackId, "LOSSLESS", expectedDurationSeconds, cancellationToken);
            }
        }
        else
        {
            manifest = JsonSerializer.Deserialize<TidalManifest>(manifestText);
        }

        // The instance's Tidal account is not entitled to stream this track in full and only
        // serves a ~30s clip. An account without HI_RES entitlement can still stream LOSSLESS
        // in full (see #269); otherwise fail rather than land a 30s clip in the library.
        if (IsPreview(trackResponse, manifest?.DurationSeconds, expectedDurationSeconds))
        {
            var reason = trackResponse.PreviewReason ?? $"~{manifest?.DurationSeconds:0}s of ~{expectedDurationSeconds}s";

            if (quality == "HI_RES_LOSSLESS")
            {
                Logger.LogWarning(
                    "HI_RES_LOSSLESS returned a preview for track {TrackId} ({Reason}), falling back to LOSSLESS",
                    trackId, reason);
                return await GetTidalManifestAsync(trackId, "LOSSLESS", expectedDurationSeconds, cancellationToken);
            }

            throw new InvalidOperationException(
                $"Tidal instance only serves a preview of track {trackId} ({reason}). " +
                "Full tracks require an instance backed by a subscribed Tidal account.");
        }

        return (manifest, quality);
    }

    // Only flag a preview from its duration when a meaningfully longer track is expected,
    // to avoid false positives on genuinely short tracks.
    private const int PreviewMinExpectedDurationSeconds = 45;
    private const double PreviewMaxDurationRatio = 0.5;

    private static bool IsPreview(TidalTrackResponse track, double? manifestDurationSeconds, int? expectedDurationSeconds)
        => string.Equals(track.AssetPresentation, "PREVIEW", StringComparison.OrdinalIgnoreCase)
           || (manifestDurationSeconds is > 0
               && expectedDurationSeconds is > PreviewMinExpectedDurationSeconds
               && manifestDurationSeconds.Value < expectedDurationSeconds.Value * PreviewMaxDurationRatio);

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
            "HIGH" or "AAC_320" or "AAC_HIGH" => "HIGH",
            "LOW" or "AAC_96" or "AAC_LOW" => "LOW",
            _ => "HI_RES_LOSSLESS"
        };
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Determines file extension based on the manifest's mime type and codecs.
    /// FLAC-in-MP4 (DASH HI_RES_LOSSLESS) keeps the .m4a container — the audio is lossless
    /// but the bytes are fragmented MP4, not raw FLAC, so renaming to .flac would mislead players.
    /// </summary>
    private static string GetExtensionFromMimeType(string? mimeType, string? codecs = null)
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
    /// Determines the quality string for the downloaded file. When codecs indicate FLAC
    /// inside an MP4 container (DASH HI_RES_LOSSLESS), we report FLAC quality rather than AAC.
    /// </summary>
    private static string GetDownloadedQuality(string requestedQuality, string? mimeType, string? codecs = null)
    {
        var hasFlacCodec = codecs?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true;

        if (mimeType?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true || hasFlacCodec)
        {
            return requestedQuality == "HI_RES_LOSSLESS" ? "FLAC_24" : "FLAC_16";
        }

        if (mimeType?.Contains("mp4", StringComparison.OrdinalIgnoreCase) == true ||
            mimeType?.Contains("aac", StringComparison.OrdinalIgnoreCase) == true)
        {
            return requestedQuality switch
            {
                "HIGH" => "AAC_320",
                "LOW" => "AAC_96",
                _ => "AAC_320"
            };
        }

        return "MP3_320";
    }

    private async Task<Stream> GetDownloadStreamAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Add("Accept", "*/*");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await HttpResponseStream.CreateAsync(response, cancellationToken);
    }

    #endregion
}
