using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.AppleMusic;

public record AlacarteSong(
    string Id, string Title, string Artist, string? ArtistId, string Album, string? AlbumId,
    string? AlbumArtist, int? Track, int? Disc, long? DurationMs, string? Isrc, int? Year,
    string? ReleaseDate, string? Genre, string? Composer, bool Explicit,
    string? ArtworkUrl, string? ArtworkUrlLarge, bool InLibrary = false);

public record AlacarteAlbum(
    string Id, string Title, string Artist, string? ArtistId, int? Year, int? TrackCount,
    string? Genre, string? ReleaseType, string? Upc, string? Label, string? Copyright,
    string? ArtworkUrl, string? ArtworkUrlLarge, List<AlacarteSong>? Tracks, bool InLibrary = false);

public record AlacarteArtist(
    string Id, string Name, string? ArtworkUrl, string? ArtworkUrlLarge, List<AlacarteAlbum>? Albums);

public record AlacartePlaylist(
    string Id, string Name, string? Curator, string? Description, int? TrackCount,
    string? ArtworkUrl, string? ArtworkUrlLarge, List<AlacarteSong>? Tracks);

public record AlacarteSearch(
    List<AlacarteSong> Songs, List<AlacarteAlbum> Albums, List<AlacarteArtist> Artists,
    List<AlacartePlaylist> Playlists);

/// <param name="Path">Song path relative to the music library root</param>
public record AlacarteEnsureResult(string Status, string Path, string? JobId);

/// <summary>
/// Client for ALACarte's /api/integration/v1 API.
/// </summary>
public class AlacarteClient
{
    public const string HttpClientName = "AppleMusic";
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly AppleMusicSettings _settings;
    private readonly ILogger<AlacarteClient> _logger;

    public AlacarteClient(IHttpClientFactory httpClientFactory, IOptions<AppleMusicSettings> settings, ILogger<AlacarteClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _http = httpClientFactory.CreateClient(HttpClientName);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.AlacarteUrl) && !string.IsNullOrWhiteSpace(_settings.ApiToken);

    public static void ConfigureClient(IServiceProvider services, HttpClient client)
    {
        var settings = services.GetRequiredService<IOptions<AppleMusicSettings>>().Value;
        if (!string.IsNullOrWhiteSpace(settings.AlacarteUrl))
        {
            client.BaseAddress = new Uri(settings.AlacarteUrl.TrimEnd('/') + "/api/integration/v1/");
        }
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        }
        // Per-request timeouts below; ensure waits for a whole download.
        client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public Task<AlacarteSearch?> SearchAsync(string query, int songs, int albums, int artists, int playlists) =>
        GetAsync<AlacarteSearch>(
            $"search?q={Uri.EscapeDataString(query)}&songs={songs}&albums={albums}&artists={artists}&playlists={playlists}");

    public Task<AlacarteSong?> GetSongAsync(string id) => GetAsync<AlacarteSong>($"songs/{Uri.EscapeDataString(id)}");

    public Task<AlacarteAlbum?> GetAlbumAsync(string id) => GetAsync<AlacarteAlbum>($"albums/{Uri.EscapeDataString(id)}");

    public Task<AlacarteArtist?> GetArtistAsync(string id) => GetAsync<AlacarteArtist>($"artists/{Uri.EscapeDataString(id)}");

    public Task<AlacartePlaylist?> GetPlaylistAsync(string id) => GetAsync<AlacartePlaylist>($"playlists/{Uri.EscapeDataString(id)}");

    /// <summary>
    /// Asks ALACarte to make sure the song is in the library, downloading it if
    /// needed, and returns its library-relative path once it is there.
    /// </summary>
    public async Task<AlacarteEnsureResult> EnsureSongAsync(string id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _settings.DownloadTimeoutSeconds)));
        using var response = await _http.PostAsync($"songs/{Uri.EscapeDataString(id)}/ensure", null, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ALACarte could not download Apple Music song {id} ({(int)response.StatusCode}): {ErrorMessage(body)}");
        }
        return JsonSerializer.Deserialize<AlacarteEnsureResult>(body, JsonOptions)
            ?? throw new InvalidOperationException("ALACarte returned an empty ensure response");
    }

    private async Task<T?> GetAsync<T>(string path) where T : class
    {
        if (!IsConfigured) return null;
        using var timeout = new CancellationTokenSource(LookupTimeout);
        try
        {
            using var response = await _http.GetAsync(path, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ALACarte {Path} failed ({Status}): {Error}", path, (int)response.StatusCode, ErrorMessage(body));
                return null;
            }
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "ALACarte {Path} request failed", path);
            return null;
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)) return error.GetString() ?? body;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}
