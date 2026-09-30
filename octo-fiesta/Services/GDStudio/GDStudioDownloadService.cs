using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.GDStudio;

public class GDStudioDownloadService : BaseDownloadService
{
    private readonly HttpClient _http;

    private readonly GDStudioSettings _settings;

    public GDStudioDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<GDStudioSettings> settings,
        IServiceProvider serviceProvider,
        ILogger<GDStudioDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _http = httpClientFactory.CreateClient(GDStudioHttpClientConfiguration.ClientName);
        _settings = settings.Value;
    }

    private const string AlbumPrefix = "ext-gdstudio-album-";

    protected override string ProviderName => GDStudioMetadataService.ProviderName;

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
        => albumId.StartsWith(AlbumPrefix) ? albumId[AlbumPrefix.Length..] : null;

    // No quality choice: we always take the best on offer, so never re-download for an upgrade.
    protected override string? GetTargetQuality() => null;

    public override async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var r = await _http.GetAsync(_settings.Url($"types=search&source={Uri.EscapeDataString(_settings.Sources[0])}&name=a&count=1"));
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        // Configured br, then the next lower one once (unknown values behave like 999).
        var (source, id) = _settings.SplitTrackId(trackId);
        var valid = GDStudioSettings.ValidBr;
        var idx = Array.IndexOf(valid, _settings.Br);
        if (idx < 0) idx = valid.Length - 1;
        int[] attempts = idx > 0 ? [valid[idx], valid[idx - 1]] : [valid[idx]];
        foreach (var br in attempts)
        {
            var info = await _http.GetFromJsonAsync<UrlResponse>(
                _settings.Url($"types=url&source={Uri.EscapeDataString(source)}&id={Uri.EscapeDataString(id)}&br={br}"),
                cancellationToken);
            if (string.IsNullOrEmpty(info?.Url)) continue;

            var response = await _http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                continue;
            }

            var ext = Path.GetExtension(new Uri(info.Url).AbsolutePath).ToLowerInvariant();
            if (ext is not (".flac" or ".mp3" or ".m4a" or ".aac" or ".ogg")) ext = info.Br >= 740 ? ".flac" : ".mp3";
            var quality = ext == ".flac" ? "FLAC" : info.Br >= 320 ? "MP3_320" : info.Br >= 192 ? "MP3_192" : "MP3_128";

            var stream = await HttpResponseStream.CreateAsync(response, cancellationToken);
            return new DownloadResult(stream, ext, quality);
        }
        throw new Exception($"GDStudio returned no downloadable url for track {trackId} (source {source})");
    }

    private record UrlResponse(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("br")] int Br);
}
