using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using Microsoft.Extensions.Options;

namespace octo_fiesta.Services.JioSaavn;

public class JioSaavnDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly JioSaavnSettings _jioSaavnSettings;
    
    private const string RhythmaxSongBaseUrl = "https://sda.rhythmax.workers.dev/song?url=";
    private const string JioSaavnDesKey = "38346591";

    protected override string ProviderName => "jiosaavn";

    public JioSaavnDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<JioSaavnSettings> jioSaavnSettings,
        IServiceProvider serviceProvider,
        ILogger<JioSaavnDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _jioSaavnSettings = jioSaavnSettings.Value;
    }

    public override async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("https://rtmx.vercel.app/api/songs?q=ping");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "JioSaavn service not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-jiosaavn-album-";
        return albumId.StartsWith(prefix) ? albumId[prefix.Length..] : null;
    }

    protected override string? GetTargetQuality() => _jioSaavnSettings.Quality;

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var songUrl = $"https://www.jiosaavn.com/song/track/{trackId}";
        var apiUrl = $"{RhythmaxSongBaseUrl}{Uri.EscapeDataString(songUrl)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("more_info", out var moreInfo) || 
            !moreInfo.TryGetProperty("encrypted_media_url", out var encryptedMediaUrlElement))
        {
            throw new Exception("Failed to extract encrypted_media_url from JioSaavn response");
        }
        
        var encryptedMediaUrl = encryptedMediaUrlElement.GetString()!;
        var decryptedUrl = DecryptMediaUrl(encryptedMediaUrl, _jioSaavnSettings.Quality);
        
        Logger.LogInformation("Got decrypted download URL for track {TrackId}: {Title}", trackId, song.Title);

        var downloadStream = await GetDownloadStreamAsync(decryptedUrl, cancellationToken);
        
        var codec = _jioSaavnSettings.Quality == "320" ? "AAC_320" : $"AAC_{_jioSaavnSettings.Quality}";
        return new DownloadResult(downloadStream, ".m4a", codec);
    }

    private static string DecryptMediaUrl(string encryptedMediaUrl, string bitrate)
    {
        var keyBytes = Encoding.UTF8.GetBytes(JioSaavnDesKey);
        var encryptedBytes = Convert.FromBase64String(encryptedMediaUrl);

        using var des = DES.Create();
        des.Key = keyBytes;
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;

        using var transform = des.CreateDecryptor();
        var decryptedBytes = transform.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        
        var decryptedLink = Encoding.UTF8.GetString(decryptedBytes);
        return decryptedLink.Replace("_96", $"_{bitrate}");
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
}
