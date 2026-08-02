using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.JioSaavn;

/// <summary>
/// HTTP client for JioSaavn upstream APIs and encrypted media URL decryption.
/// </summary>
public sealed class JioSaavnApiClient
{
    private const string SearchBaseUrl = "https://rtmx.vercel.app/api/songs?q=";
    private const string SongDetailsBaseUrl = "https://sda.rhythmax.workers.dev/song?url=";
    private static readonly byte[] DesKey = Encoding.ASCII.GetBytes("38346591");
    private static readonly Regex QualitySuffixRegex = new(@"_\d+\.mp4(\?.*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly JioSaavnSettings _settings;

    public JioSaavnApiClient(IHttpClientFactory httpClientFactory, IOptions<JioSaavnSettings> settings)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
    }

    public async Task<IReadOnlyList<JioSaavnApiSong>> SearchSongsAsync(string query, int? limit = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query cannot be null or empty.", nameof(query));
        }

        var encodedQuery = Uri.EscapeDataString(query.Trim());
        using var response = await _httpClient.GetAsync($"{SearchBaseUrl}{encodedQuery}", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<JioSaavnSearchResponse>(contentStream, SerializerOptions, cancellationToken);
        if (payload?.Results is null || payload.Results.Count == 0)
        {
            return [];
        }

        var effectiveLimit = Math.Clamp(limit ?? 20, 1, 100);
        return payload.Results.Take(effectiveLimit).ToList();
    }

    public async Task<JioSaavnApiSong?> GetSongDetailsByPermaUrlAsync(string permaUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(permaUrl))
        {
            throw new ArgumentException("Song URL cannot be null or empty.", nameof(permaUrl));
        }

        var encodedSongUrl = Uri.EscapeDataString(permaUrl.Trim());
        using var response = await _httpClient.GetAsync($"{SongDetailsBaseUrl}{encodedSongUrl}", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<JioSaavnApiSong>(contentStream, SerializerOptions, cancellationToken);
    }

    public Task<JioSaavnApiSong?> GetSongDetailsByExternalIdAsync(string externalSongId, CancellationToken cancellationToken = default)
    {
        var permaUrl = DecodePermaUrlExternalId(externalSongId);
        return GetSongDetailsByPermaUrlAsync(permaUrl, cancellationToken);
    }

    public async Task<string?> ResolveQualityMediaUrlAsyncByExternalId(string externalSongId, int? kbps = null, CancellationToken cancellationToken = default)
    {
        var song = await GetSongDetailsByExternalIdAsync(externalSongId, cancellationToken);
        var encryptedMediaUrl = song?.MoreInfo?.EncryptedMediaUrl;
        if (string.IsNullOrWhiteSpace(encryptedMediaUrl))
        {
            return null;
        }

        var decryptedMediaUrl = DecryptMediaUrl(encryptedMediaUrl);
        return GetQualityUrl(decryptedMediaUrl, kbps ?? GetConfiguredQualityKbps());
    }

    public static string EncodePermaUrlExternalId(string permaUrl)
    {
        if (string.IsNullOrWhiteSpace(permaUrl))
        {
            throw new ArgumentException("Perma URL cannot be null or empty.", nameof(permaUrl));
        }

        var bytes = Encoding.UTF8.GetBytes(permaUrl.Trim());
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string DecodePermaUrlExternalId(string externalSongId)
    {
        if (string.IsNullOrWhiteSpace(externalSongId))
        {
            throw new ArgumentException("External song ID cannot be null or empty.", nameof(externalSongId));
        }

        var normalized = externalSongId.Trim().Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding != 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        var bytes = Convert.FromBase64String(normalized);
        return Encoding.UTF8.GetString(bytes);
    }

    public static string DecryptMediaUrl(string encryptedMediaUrl)
    {
        if (string.IsNullOrWhiteSpace(encryptedMediaUrl))
        {
            throw new ArgumentException("Encrypted media URL cannot be null or empty.", nameof(encryptedMediaUrl));
        }

        var normalizedBase64 = NormalizeBase64(encryptedMediaUrl.Trim());
        var encryptedBytes = Convert.FromBase64String(normalizedBase64);

        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        des.Key = DesKey;

        using var decryptor = des.CreateDecryptor();
        var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        return Encoding.UTF8.GetString(decryptedBytes);
    }

    public static string GetQualityUrl(string decryptedMediaUrl, int kbps)
    {
        if (string.IsNullOrWhiteSpace(decryptedMediaUrl))
        {
            throw new ArgumentException("Decrypted media URL cannot be null or empty.", nameof(decryptedMediaUrl));
        }

        var quality = Math.Clamp(kbps, 12, 320);
        return QualitySuffixRegex.Replace(decryptedMediaUrl, $"_{quality}.mp4$1");
    }

    private static string NormalizeBase64(string base64)
    {
        var normalized = base64.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        return padding == 0 ? normalized : normalized.PadRight(normalized.Length + (4 - padding), '=');
    }

    private int GetConfiguredQualityKbps()
    {
        return int.TryParse(_settings.Quality, out var parsed)
            ? Math.Clamp(parsed, 12, 320)
            : 320;
    }
}

public sealed class JioSaavnSearchResponse
{
    [JsonPropertyName("results")]
    public List<JioSaavnApiSong> Results { get; set; } = [];
}

public sealed class JioSaavnApiSong
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("perma_url")]
    public string? PermaUrl { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("isExplicit")]
    public bool IsExplicit { get; set; }

    [JsonPropertyName("more_info")]
    public JioSaavnApiSongMoreInfo? MoreInfo { get; set; }
}

public sealed class JioSaavnApiSongMoreInfo
{
    [JsonPropertyName("album_id")]
    public string? AlbumId { get; set; }

    [JsonPropertyName("album_token")]
    public string? AlbumToken { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("album_url")]
    public string? AlbumUrl { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("encrypted_media_url")]
    public string? EncryptedMediaUrl { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    [JsonPropertyName("copyright_text")]
    public string? CopyrightText { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("artists")]
    public JioSaavnApiArtists? Artists { get; set; }
}

public sealed class JioSaavnApiArtists
{
    [JsonPropertyName("primary")]
    public List<JioSaavnApiArtist> Primary { get; set; } = [];

    [JsonPropertyName("featured")]
    public List<JioSaavnApiArtist> Featured { get; set; } = [];
}

public sealed class JioSaavnApiArtist
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("artist_token")]
    public string? ArtistToken { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("perma_url")]
    public string? PermaUrl { get; set; }
}
