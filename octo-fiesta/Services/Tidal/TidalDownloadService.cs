using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Tidal;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.Tidal;

/// <summary>
/// Download service backed by Tidal's own API. What can actually be streamed is decided by
/// the account's subscription, so a refused quality walks down the tier ladder instead of
/// failing outright.
/// </summary>
public class TidalDownloadService : BaseDownloadService
{
    private const string AlbumIdPrefix = "ext-tidal-album-";

    /// <summary>
    /// Only treat a short manifest as a preview when a meaningfully longer track is
    /// expected, to avoid false positives on genuinely short tracks.
    /// </summary>
    private const int PreviewMinExpectedDurationSeconds = 45;
    private const double PreviewMaxDurationRatio = 0.5;

    private readonly HttpClient _apiClient;
    private readonly HttpClient _mediaClient;
    private readonly TidalAuthService _auth;
    private readonly string _preferredQuality;

    protected override string ProviderName => TidalMetadataService.ProviderName;

    public TidalDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<TidalSettings> tidalSettings,
        TidalAuthService auth,
        IServiceProvider serviceProvider,
        ILogger<TidalDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _apiClient = httpClientFactory.CreateClient(TidalHttpClientConfiguration.AuthClientName);
        _mediaClient = httpClientFactory.CreateClient(TidalHttpClientConfiguration.MediaClientName);
        _auth = auth;
        _preferredQuality = TidalQuality.Normalize(tidalSettings.Value.Quality);
    }

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        if (!_auth.IsConfigured)
        {
            Logger.LogWarning("Tidal is not authenticated. Run the login helper with --tidal-login");
            return false;
        }

        try
        {
            return await _auth.GetSessionAsync() is not null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Tidal service not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
        => albumId.StartsWith(AlbumIdPrefix) ? albumId[AlbumIdPrefix.Length..] : null;

    protected override string? GetTargetQuality() => _preferredQuality;

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var (manifest, quality) = await GetManifestAsync(trackId, _preferredQuality, song.Duration, cancellationToken);

        if (manifest?.Urls is null || manifest.Urls.Count == 0)
        {
            throw new InvalidOperationException($"The Tidal manifest for track {trackId} holds no stream URL.");
        }

        Stream stream;
        if (manifest.Urls.Count > 1)
        {
            Logger.LogInformation(
                "Downloading {SegmentCount} DASH segments for track {TrackId}: {Title} (quality: {Quality}, codecs: {Codecs})",
                manifest.Urls.Count, trackId, song.Title, quality, manifest.Codecs ?? "?");
            stream = new MultiSegmentHttpStream(_mediaClient, manifest.Urls);
        }
        else
        {
            Logger.LogInformation(
                "Got download URL for track {TrackId}: {Title} (quality: {Quality})",
                trackId, song.Title, quality);
            stream = await OpenStreamAsync(manifest.Urls[0], cancellationToken);
        }

        if (!string.IsNullOrEmpty(manifest.KeyId))
        {
            Logger.LogInformation("Track {TrackId} is served encrypted, decrypting on the fly", trackId);
            stream = TidalStreamDecryptor.Decrypt(stream, manifest.KeyId);
        }

        var extension = TidalQuality.GetExtension(manifest.MimeType, manifest.Codecs);
        var downloadedQuality = TidalQuality.GetDownloadedQuality(quality, manifest.MimeType, manifest.Codecs);

        // A fMP4 assembled from DASH segments has a moov duration of 0, so scanners report
        // 0:00 unless it is patched with the duration we already know.
        var mp4Duration = extension == ".m4a"
            ? manifest.DurationSeconds ?? song.Duration
            : null;

        return new DownloadResult(stream, extension, downloadedQuality, mp4Duration);
    }

    #endregion

    #region Manifest

    /// <summary>
    /// Fetches the playback manifest for a track, falling back to the next quality tier
    /// whenever Tidal refuses the requested one or answers with a preview clip.
    /// Handles both the BTS JSON manifest and the DASH MPD served for HI_RES_LOSSLESS,
    /// flattening DASH segments into the same URL list with the init segment first.
    /// </summary>
    internal async Task<(TidalManifest? Manifest, string Quality)> GetManifestAsync(
        string trackId, string quality, int? expectedDurationSeconds, CancellationToken cancellationToken)
    {
        var playbackInfo = await GetPlaybackInfoAsync(trackId, quality, cancellationToken);

        if (playbackInfo is null || string.IsNullOrEmpty(playbackInfo.Manifest))
        {
            return await FallBackOrFailAsync(trackId, quality, expectedDurationSeconds, cancellationToken,
                $"Tidal returned no playable manifest for track {trackId} at quality {quality}.");
        }

        var manifestText = Encoding.UTF8.GetString(Convert.FromBase64String(playbackInfo.Manifest));
        var manifestMimeType = playbackInfo.ManifestMimeType ?? "";

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
                    DurationSeconds = parsed.DurationSeconds
                };
            }
            catch (Exception ex)
            {
                return await FallBackOrFailAsync(trackId, quality, expectedDurationSeconds, cancellationToken,
                    $"Failed to parse the DASH manifest for track {trackId} at quality {quality}: {ex.Message}");
            }
        }
        else
        {
            manifest = JsonSerializer.Deserialize<TidalManifest>(manifestText);
        }

        // A track the account cannot stream in full comes back as a ~30s clip. Fail rather
        // than land a preview in the library.
        if (IsPreview(playbackInfo, manifest?.DurationSeconds, expectedDurationSeconds))
        {
            var reason = playbackInfo.AssetPresentation ?? $"~{manifest?.DurationSeconds:0}s of ~{expectedDurationSeconds}s";
            return await FallBackOrFailAsync(trackId, quality, expectedDurationSeconds, cancellationToken,
                $"Tidal only serves a preview of track {trackId} at quality {quality} ({reason}). "
                + "Full tracks require an active subscription.");
        }

        // Tidal may quietly serve a lower tier than the one asked for, so report what it
        // actually delivered rather than what was requested.
        return (manifest, playbackInfo.AudioQuality ?? quality);
    }

    private async Task<TidalPlaybackInfo?> GetPlaybackInfoAsync(
        string trackId, string quality, CancellationToken cancellationToken)
    {
        var countryCode = await _auth.GetCountryCodeAsync(cancellationToken);
        var url = $"{TidalHttpClientConfiguration.ApiBaseUrl}/tracks/{trackId}/playbackinfopostpaywall"
                  + $"?audioquality={quality}&playbackmode=STREAM&assetpresentation=FULL"
                  + $"&countryCode={countryCode}";

        using var request = await _auth.CreateAuthenticatedRequestAsync(HttpMethod.Get, url, cancellationToken);
        using var response = await _apiClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Logger.LogWarning(
                "Tidal playback info for track {TrackId} at quality {Quality} returned {StatusCode}: {Body}",
                trackId, quality, response.StatusCode, body);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TidalPlaybackInfo>(json);
    }

    /// <summary>
    /// Retries one quality tier lower, or gives up when already at the lowest one.
    /// </summary>
    private async Task<(TidalManifest? Manifest, string Quality)> FallBackOrFailAsync(
        string trackId, string quality, int? expectedDurationSeconds, CancellationToken cancellationToken, string reason)
    {
        var lower = TidalQuality.NextLower(quality);
        if (lower is null)
        {
            // Tidal normally serves a lower tier rather than refusing, so reaching the bottom
            // of the ladder means the account cannot stream this track at all.
            throw new InvalidOperationException(
                $"{reason} No quality tier could be streamed for track {trackId}. "
                + "The account has no playback entitlement for it.");
        }

        Logger.LogWarning("{Reason} Falling back to {Quality}", reason, lower);
        return await GetManifestAsync(trackId, lower, expectedDurationSeconds, cancellationToken);
    }

    private static bool IsPreview(TidalPlaybackInfo playbackInfo, double? manifestDurationSeconds, int? expectedDurationSeconds)
        => string.Equals(playbackInfo.AssetPresentation, "PREVIEW", StringComparison.OrdinalIgnoreCase)
           || (manifestDurationSeconds is > 0
               && expectedDurationSeconds is > PreviewMinExpectedDurationSeconds
               && manifestDurationSeconds.Value < expectedDurationSeconds.Value * PreviewMaxDurationRatio);

    private async Task<Stream> OpenStreamAsync(string url, CancellationToken cancellationToken)
    {
        // Manifest URLs are pre-signed CDN links and must not carry the account's token.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _mediaClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await HttpResponseStream.CreateAsync(response, cancellationToken);
    }

    #endregion
}
