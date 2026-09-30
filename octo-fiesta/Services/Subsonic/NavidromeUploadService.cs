using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Subsonic;

/// <summary>
/// Result of a successful upload to Navidrome's custom /api/upload endpoint.
/// </summary>
public record NavidromeUploadResult(string Id, string Path, int LibraryId, string? Title);

/// <summary>
/// Talks to a forked Navidrome that exposes <c>POST /api/upload</c>.
/// Authentication is handled by logging in once via <c>POST /auth/login</c> using the configured
/// admin credentials. The resulting JWT is cached and reused; on a 401 response the token is
/// dropped and re-acquired automatically on the next call.
/// </summary>
public interface INavidromeUploadService
{
    /// <summary>
    /// True when the feature is enabled and the required configuration is present.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Uploads <paramref name="localFilePath"/> to Navidrome inside <paramref name="folder"/>
    /// (relative to the library root) using <paramref name="fileName"/> as the destination filename.
    /// Returns the parsed response or <c>null</c> on failure.
    /// </summary>
    Task<NavidromeUploadResult?> UploadFileAsync(
        string localFilePath,
        string folder,
        string fileName,
        CancellationToken cancellationToken = default);
}

public class NavidromeUploadService : INavidromeUploadService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SubsonicSettings _settings;
    private readonly ILogger<NavidromeUploadService> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;

    public NavidromeUploadService(
        IHttpClientFactory httpClientFactory,
        IOptions<SubsonicSettings> settings,
        ILogger<NavidromeUploadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _settings.UseNavidromeUploadApi
        && !string.IsNullOrWhiteSpace(_settings.Url)
        && !string.IsNullOrWhiteSpace(_settings.AdminUsername)
        && !string.IsNullOrWhiteSpace(_settings.AdminPassword);

    public async Task<NavidromeUploadResult?> UploadFileAsync(
        string localFilePath,
        string folder,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Navidrome upload requested but feature is not configured");
            return null;
        }

        if (!File.Exists(localFilePath))
        {
            _logger.LogError("Cannot upload, file does not exist: {Path}", localFilePath);
            return null;
        }

        // Try once with the cached token, retry once after re-login on 401
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(forceRefresh: attempt > 0, cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            try
            {
                var response = await SendUploadAsync(token, localFilePath, folder, fileName, cancellationToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _logger.LogInformation("Navidrome upload returned 401, refreshing JWT and retrying");
                    InvalidateToken();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError(
                        "Navidrome upload failed: {StatusCode} - {Body}",
                        response.StatusCode,
                        Truncate(body, 500));
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseResponse(json);
            }
            catch (HttpRequestException ex) when (attempt == 0 && IsBrokenPipe(ex))
            {
                // Navidrome rejected the request (likely 401) and closed the connection while
                // the multipart body was still streaming, producing a broken-pipe IOException
                // before we could read a response. Treat this as an auth failure: drop the
                // cached token and retry once with a fresh JWT.
                _logger.LogInformation(
                    "Navidrome upload connection broken mid-stream (likely 401); refreshing JWT and retrying");
                InvalidateToken();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error uploading to Navidrome /api/upload");
                return null;
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendUploadAsync(
        string token,
        string localFilePath,
        string folder,
        string fileName,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        // No timeout - large FLACs over slow connections may take a while
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(_settings.NavidromeLibraryId.ToString()), "libraryId");
        content.Add(new StringContent(folder ?? string.Empty), "folder");

        var fileStream = File.OpenRead(localFilePath);
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(fileName));
        content.Add(streamContent, "file", fileName);

        var url = $"{_settings.Url!.TrimEnd('/')}/api/upload";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("x-nd-authorization", $"Bearer {token}");

        _logger.LogDebug(
            "Uploading {FileName} ({Size} bytes) to Navidrome library {LibraryId} folder '{Folder}'",
            fileName, new FileInfo(localFilePath).Length, _settings.NavidromeLibraryId, folder);

        // The HttpClient owns and disposes the request/content (which closes fileStream)
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private NavidromeUploadResult? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            var path = root.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
            var libraryId = root.TryGetProperty("libraryId", out var lEl) && lEl.ValueKind == JsonValueKind.Number
                ? lEl.GetInt32()
                : _settings.NavidromeLibraryId;
            var title = root.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path))
            {
                _logger.LogWarning("Navidrome upload returned 200 but response missing id/path: {Body}", Truncate(json, 500));
                return null;
            }

            _logger.LogInformation("Navidrome upload accepted: id={Id} path={Path}", id, path);
            return new NavidromeUploadResult(id, path, libraryId, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Navidrome upload response: {Body}", Truncate(json, 500));
            return null;
        }
    }

    private async Task<string?> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && !string.IsNullOrEmpty(_cachedToken))
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && !string.IsNullOrEmpty(_cachedToken))
            {
                return _cachedToken;
            }

            var token = await LoginAsync(cancellationToken);
            _cachedToken = token;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void InvalidateToken()
    {
        _cachedToken = null;
    }

    private async Task<string?> LoginAsync(CancellationToken cancellationToken)
    {
        var url = $"{_settings.Url!.TrimEnd('/')}/auth/login";
        var payload = JsonSerializer.Serialize(new
        {
            username = _settings.AdminUsername,
            password = _settings.AdminPassword,
        });

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Navidrome /auth/login failed: {StatusCode} - {Body}",
                    response.StatusCode,
                    Truncate(body, 300));
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("token", out var tokenEl))
            {
                _logger.LogError("Navidrome /auth/login returned no token field: {Body}", Truncate(json, 300));
                return null;
            }

            var token = tokenEl.GetString();
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("Navidrome /auth/login returned empty token");
                return null;
            }

            // Best effort sanity check that we logged in as admin
            if (doc.RootElement.TryGetProperty("isAdmin", out var adminEl)
                && adminEl.ValueKind == JsonValueKind.False)
            {
                _logger.LogWarning(
                    "Navidrome login user '{User}' is not admin; /api/upload will likely reject the request",
                    _settings.AdminUsername);
            }

            _logger.LogInformation("Acquired Navidrome JWT for user '{User}'", _settings.AdminUsername);
            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log in to Navidrome at {Url}", url);
            return null;
        }
    }

    private static string GuessMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".flac" => "audio/flac",
            ".mp3" => "audio/mpeg",
            ".m4a" or ".aac" => "audio/aac",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".wav" => "audio/wav",
            _ => "application/octet-stream",
        };
    }

    private static bool IsBrokenPipe(Exception ex)
    {
        for (var e = ex as Exception; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.Shutdown or System.Net.Sockets.SocketError.ConnectionReset }
                || (e is System.Net.Sockets.SocketException se && (int)se.SocketErrorCode == 32) // EPIPE
                || (e is IOException && e.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase))
                || (e is IOException && e.Message.Contains("transport connection", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
