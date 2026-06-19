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
/// Supports both Qobuz and Tidal backends with automatic instance failover for Tidal
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
    private const string AmazonBaseUrl = "https://amz.squid.wtf";

    // Required headers
    private const string QobuzCountryHeader = "Token-Country";
    private const string QobuzCountryValue = "US";
    private const string TidalClientHeader = "x-client";
    private const string TidalClientValue = "BiniLossless/v3.4";
    private const string AmazonCaptchaTokenHeader = "X-Captcha-Token";

    // Quality mappings
    // Qobuz: 27 = FLAC 24-bit/192kHz, 7 = FLAC 24-bit/96kHz, 6 = FLAC 16-bit/44kHz, 5 = MP3 320kbps
    // Tidal: HI_RES_LOSSLESS (FLAC 24-bit), LOSSLESS (FLAC 16-bit), HIGH (320kbps AAC), LOW (96kbps AAC)
    // Amazon: best (FLAC 24-bit), hd (FLAC 16-bit), standard (AAC 256kbps), opus (Opus), atmos (Dolby Atmos)

    private bool IsQobuzSource => _squidWTFSettings.Source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase);
    private bool IsAmazonSource => _squidWTFSettings.Source.Equals("AmazonMusic", StringComparison.OrdinalIgnoreCase);

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

            if (IsAmazonSource)
            {
                // Verify captcha challenge endpoint is reachable
                var response = await _httpClient.GetAsync($"{AmazonBaseUrl}/api/captcha/challenge");
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
        if (IsAmazonSource) return "ultrahd";
        return "HI_RES_LOSSLESS";
    }

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        if (IsQobuzSource)
            return await DownloadTrackQobuzAsync(trackId, song, cancellationToken);
        if (IsAmazonSource)
            return await DownloadTrackAmazonAsync(trackId, song, cancellationToken);
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

    #region Amazon Music Download

    private async Task<DownloadResult> DownloadTrackAmazonAsync(string trackAsin, Song song, CancellationToken cancellationToken)
    {
        var tier = GetAmazonTier();
        var country = _squidWTFSettings.Country;

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            bool forceRefresh = attempt > 1;
            var token = await _captchaSolver.GetAmazonCaptchaTokenAsync(AmazonBaseUrl, forceRefresh: forceRefresh, cancellationToken);
            var trackResponse = await FetchAmazonTrackAsync(trackAsin, tier, country, token, cancellationToken);

            if (trackResponse == null)
            {
                Logger.LogWarning("Amazon Music track request failed (attempt {Attempt}/{Max}), will refresh token", attempt, maxAttempts);
                continue;
            }

            if (string.IsNullOrEmpty(trackResponse.Stream?.Url))
            {
                Logger.LogWarning("Amazon Music returned no stream URL (attempt {Attempt}/{Max})", attempt, maxAttempts);
                continue;
            }

            var cencKey = trackResponse.Drm?.Key;
            Logger.LogInformation("Got Amazon Music stream URL for track {TrackAsin}: {Title} (codec: {Codec}, tier: {Tier}, attempt: {Attempt}, hasKey: {HasKey})",
                trackAsin, song.Title, trackResponse.Stream.Codec ?? "?", tier, attempt, cencKey != null);

            var streamUrl = trackResponse.Stream.Url;
            if (streamUrl.StartsWith("/")) streamUrl = $"{AmazonBaseUrl}{streamUrl}";

            try
            {
                var downloadStream = await GetAmazonStreamAsync(streamUrl, token, cancellationToken);
                var codec = (trackResponse.Stream.Codec ?? "").ToLowerInvariant();
                var (extension, quality) = GetAmazonExtensionAndQuality(codec, tier);
                var cencTarget = codec == "flac" && cencKey != null ? ".flac" : null;
                return new DownloadResult(downloadStream, extension, quality, CencKey: cencKey, CencTargetExtension: cencTarget);
            }
            catch (TimeoutException ex)
            {
                Logger.LogWarning("Amazon Music stream stalled on attempt {Attempt}/{Max}: {Message} — retrying with fresh URL", attempt, maxAttempts, ex.Message);
                // Stream URL is single-use; loop will fetch a new one
            }
        }

        throw new Exception($"Failed to download Amazon Music track {trackAsin} after {maxAttempts} attempts");
    }

    private async Task<AmazonMusicTrackResponse?> FetchAmazonTrackAsync(
        string asin, string tier, string country, string token, CancellationToken cancellationToken)
    {
        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new { asin, tier, country });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{AmazonBaseUrl}/api/track");
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            request.Headers.Add(AmazonCaptchaTokenHeader, token);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return null; // Signal to caller to refresh token
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            Logger.LogDebug("Amazon /api/track response for {Asin}: {Json}", asin, json);
            return System.Text.Json.JsonSerializer.Deserialize<AmazonMusicTrackResponse>(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Amazon Music /api/track request failed for {Asin}", asin);
            return null;
        }
    }

    private async Task<Stream> GetAmazonStreamAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(AmazonCaptchaTokenHeader, token);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Add("Accept", "*/*");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // One more attempt with a fresh token
            var freshToken = await _captchaSolver.GetAmazonCaptchaTokenAsync(AmazonBaseUrl, forceRefresh: true, cancellationToken);
            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, url);
            retryRequest.Headers.Add(AmazonCaptchaTokenHeader, freshToken);
            retryRequest.Headers.Add("User-Agent", "Mozilla/5.0");
            retryRequest.Headers.Add("Accept", "*/*");
            response = await _httpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return await HttpResponseStream.CreateAsync(response, cancellationToken);
    }

    private string GetAmazonTier()
    {
        var quality = _squidWTFSettings.Quality;

        if (string.IsNullOrEmpty(quality))
            return "best"; // Default to FLAC 24-bit

        return quality.ToUpperInvariant() switch
        {
            "FLAC_24" or "FLAC_24_192" or "ULTRAHD" or "BEST" => "best",
            "FLAC_16" or "FLAC" or "HD" => "hd",
            "AAC" or "AAC_256" or "HIGH" or "STANDARD" => "standard",
            "OPUS" => "opus",
            "ATMOS" => "atmos",
            _ => "best"
        };
    }

    private static (string Extension, string Quality) GetAmazonExtensionAndQuality(string codec, string tier)
    {
        return codec switch
        {
            "opus" => (".m4a", "OPUS_320"),
            "atmos" => (".m4a", "ATMOS"),
            _ => tier switch
            {
                "best" => (".m4a", "FLAC_24"),
                "hd" => (".m4a", "FLAC_16"),
                "standard" => (".m4a", "AAC_256"),
                _ => (".m4a", "FLAC_24")
            }
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

        if (manifestMimeType.Contains("dash+xml") || manifestMimeType.Contains("application/dash"))
        {
            try
            {
                var parsed = TidalDashManifestParser.Parse(manifestText);
                Logger.LogInformation(
                    "Parsed DASH manifest for track {TrackId}: {SegmentCount} segments, codecs={Codecs}",
                    trackId, parsed.Urls.Count, parsed.Codecs);

                // Account without HI_RES entitlement gets a ~30s preview, not the full track;
                // fall back to LOSSLESS which it can stream in full. (see #269)
                if (quality == "HI_RES_LOSSLESS"
                    && IsPreviewManifest(parsed.DurationSeconds, expectedDurationSeconds))
                {
                    Logger.LogWarning(
                        "HI_RES_LOSSLESS returned a ~{PreviewDuration:0}s preview for track {TrackId} " +
                        "(expected ~{Expected}s), falling back to LOSSLESS",
                        parsed.DurationSeconds, trackId, expectedDurationSeconds);
                    return await GetTidalManifestAsync(trackId, "LOSSLESS", expectedDurationSeconds, cancellationToken);
                }

                var manifest = new TidalManifest
                {
                    MimeType = parsed.MimeType ?? "audio/mp4",
                    Codecs = parsed.Codecs,
                    Urls = parsed.Urls.ToList(),
                    DurationSeconds = parsed.DurationSeconds,
                };
                return (manifest, quality);
            }
            catch (Exception ex) when (quality == "HI_RES_LOSSLESS")
            {
                Logger.LogWarning(ex,
                    "Failed to parse HI_RES_LOSSLESS DASH manifest for track {TrackId}, falling back to LOSSLESS",
                    trackId);
                return await GetTidalManifestAsync(trackId, "LOSSLESS", expectedDurationSeconds, cancellationToken);
            }
        }

        var jsonManifest = JsonSerializer.Deserialize<TidalManifest>(manifestText);
        return (jsonManifest, quality);
    }

    // Only flag a preview when a meaningfully longer track is expected, to avoid false
    // positives on genuinely short tracks.
    private const int PreviewMinExpectedDurationSeconds = 45;
    private const double PreviewMaxDurationRatio = 0.5;

    private static bool IsPreviewManifest(double? manifestDurationSeconds, int? expectedDurationSeconds)
        => manifestDurationSeconds is > 0
           && expectedDurationSeconds is > PreviewMinExpectedDurationSeconds
           && manifestDurationSeconds.Value < expectedDurationSeconds.Value * PreviewMaxDurationRatio;

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

    /// <summary>
    /// Read-only forward Stream that concatenates the bodies of multiple HTTP GETs.
    /// Used for DASH downloads: init segment + N media segments must be reassembled in order.
    /// </summary>
    internal sealed class MultiSegmentHttpStream : Stream
    {
        private readonly HttpClient _http;
        private readonly IReadOnlyList<string> _urls;
        private int _index = -1;
        private HttpResponseMessage? _currentResponse;
        private Stream? _currentStream;

        public MultiSegmentHttpStream(HttpClient http, IReadOnlyList<string> urls)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _urls = urls ?? throw new ArgumentNullException(nameof(urls));
            if (_urls.Count == 0)
            {
                throw new ArgumentException("At least one segment URL is required", nameof(urls));
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_currentStream == null)
                {
                    if (!await AdvanceAsync(cancellationToken).ConfigureAwait(false)) return 0;
                }

                var read = await _currentStream!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0) return read;
                await DisposeCurrentAsync().ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private async Task<bool> AdvanceAsync(CancellationToken cancellationToken)
        {
            _index++;
            if (_index >= _urls.Count) return false;

            using var request = new HttpRequestMessage(HttpMethod.Get, _urls[_index]);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            request.Headers.Accept.ParseAdd("*/*");

            _currentResponse = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _currentResponse.EnsureSuccessStatusCode();
            _currentStream = await _currentResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task DisposeCurrentAsync()
        {
            if (_currentStream != null)
            {
                await _currentStream.DisposeAsync().ConfigureAwait(false);
                _currentStream = null;
            }
            _currentResponse?.Dispose();
            _currentResponse = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _currentStream?.Dispose();
                _currentResponse?.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await DisposeCurrentAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
