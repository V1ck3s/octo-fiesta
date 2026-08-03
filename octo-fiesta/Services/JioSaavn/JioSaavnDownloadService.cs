using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.JioSaavn;

public sealed class JioSaavnDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly JioSaavnApiClient _apiClient;
    private readonly JioSaavnSettings _jioSaavnSettings;

    protected override string ProviderName => "jiosaavn";

    public JioSaavnDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<JioSaavnSettings> jioSaavnSettings,
        IServiceProvider serviceProvider,
        JioSaavnApiClient apiClient,
        ILogger<JioSaavnDownloadService> logger)
        : base(
            httpClientFactory,
            configuration,
            localLibraryService,
            metadataService,
            subsonicSettings.Value,
            serviceProvider,
            logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiClient = apiClient;
        _jioSaavnSettings = jioSaavnSettings.Value;
    }

    public override async Task<bool> IsAvailableAsync()
    {
        try
        {
            await _apiClient.SearchSongsAsync("test", 1);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "JioSaavn service is not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-jiosaavn-album-";

        return albumId.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase)
            ? albumId[prefix.Length..]
            : null;
    }

    protected override string? GetTargetQuality() =>
        GetConfiguredQualityKbps().ToString();

    protected override async Task<DownloadResult> DownloadTrackAsync(
        string trackId,
        Song song,
        CancellationToken cancellationToken)
    {
        var qualityKbps = GetConfiguredQualityKbps();

        var mediaUrl =
            await _apiClient.ResolveQualityMediaUrlAsyncByExternalId(
                trackId,
                qualityKbps,
                cancellationToken);

        if (string.IsNullOrWhiteSpace(mediaUrl))
        {
            throw new InvalidOperationException(
                $"JioSaavn did not return an encrypted media URL for track {trackId}.");
        }

        Logger.LogInformation(
            "Resolved JioSaavn {Quality} kbps stream for {TrackId}: {Title}",
            qualityKbps,
            trackId,
            song.Title);

        var downloadStream = await GetDownloadStreamAsync(
            mediaUrl,
            cancellationToken);

        return new DownloadResult(
            downloadStream,
            ".m4a",
            $"AAC_{qualityKbps}");
    }

    private int GetConfiguredQualityKbps()
    {
        return int.TryParse(_jioSaavnSettings.Quality, out var parsed)
            ? JioSaavnApiClient.NormalizeQuality(parsed)
            : 320;
    }

    private async Task<Stream> GetDownloadStreamAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        request.Headers.Accept.ParseAdd("*/*");

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await HttpResponseStream.CreateAsync(
            response,
            cancellationToken);
    }
}
