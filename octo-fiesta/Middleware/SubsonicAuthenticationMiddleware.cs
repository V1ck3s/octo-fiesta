using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Middleware;

/// <summary>
/// Outcome of validating a client's credentials against the upstream Subsonic server.
/// Distinguishing these matters for the client-facing error: "unreachable" is an
/// infrastructure problem (the Subsonic server is down), while "invalid credentials"
/// is an account problem. Conflating them (the old behavior) made Navidrome outages
/// look like bad passwords and buried the real cause in a generic log line.
/// </summary>
enum PingOutcome
{
    Valid,
    InvalidCredentials,
    Unreachable,
    UpstreamError
}

/// <summary>
/// Middleware that validates Subsonic authentication parameters by verifying them against the upstream Subsonic server.
/// This prevents unauthenticated access to external resources (like SquidWTF downloads).
/// </summary>
public class SubsonicAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SubsonicAuthenticationMiddleware> _logger;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    
    // Cache validated credentials to avoid hitting the Subsonic server on every request
    // Key: hash of credentials, Value: expiration time
    private readonly ConcurrentDictionary<string, DateTime> _validatedCredentials = new();
    private static readonly TimeSpan CredentialCacheDuration = TimeSpan.FromMinutes(5);
    
    // Negative cache: when the upstream Subsonic server is unreachable, remember it for a
    // short window so a storm of client requests does not pile up slow pings against a dead
    // server (each of which would otherwise block the client request on the HTTP timeout).
    private DateTime _upstreamDownUntil = DateTime.MinValue;
    private static readonly TimeSpan UnreachableCacheDuration = TimeSpan.FromSeconds(30);
    // Per-ping timeout: fail fast when the upstream is down instead of blocking the request.
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);
    
    // Paths that don't require authentication
    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/health",
        "/swagger",
        "/swagger/index.html",
        "/swagger/v1/swagger.json"
    };

    public SubsonicAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<SubsonicAuthenticationMiddleware> logger,
        IOptions<SubsonicSettings> subsonicSettings,
        IHttpClientFactory httpClientFactory)
    {
        _next = next;
        _logger = logger;
        _subsonicSettings = subsonicSettings.Value;
        _httpClientFactory = httpClientFactory;
    }

    public async Task InvokeAsync(HttpContext context, SubsonicRequestParser requestParser)
    {
        var path = context.Request.Path.Value ?? "";
        
        // Skip authentication for public paths
        if (IsPublicPath(path))
        {
            await _next(context);
            return;
        }
        
        // Skip authentication for non-Subsonic paths (Swagger, etc.)
        if (!path.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase) && path != "/")
        {
            await _next(context);
            return;
        }
        
        // Extract authentication parameters
        var parameters = await requestParser.ExtractAllParametersAsync(context.Request);
        
        // Reset body position for downstream middleware/controllers
        if (context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
        }
        
        var username = parameters.GetValueOrDefault("u", "");
        var hasAuth = !string.IsNullOrEmpty(username) && 
                      (!string.IsNullOrEmpty(parameters.GetValueOrDefault("p", "")) ||
                       (!string.IsNullOrEmpty(parameters.GetValueOrDefault("t", "")) && 
                        !string.IsNullOrEmpty(parameters.GetValueOrDefault("s", ""))));
        
        if (!hasAuth)
        {
            _logger.LogWarning("Authentication failed: missing credentials for {Path}", path);
            await WriteSubsonicError(context, httpStatus: 401, code: 40, "Missing authentication parameters");
            return;
        }
        
        // Check if credentials are already validated (cached)
        var credentialHash = ComputeCredentialHash(parameters);
        if (_validatedCredentials.TryGetValue(credentialHash, out var expiration) && DateTime.UtcNow < expiration)
        {
            // Credentials are cached and valid
            await _next(context);
            return;
        }
        
        // Validate credentials against the Subsonic server
        var outcome = await ValidateCredentialsAsync(parameters);
        
        switch (outcome)
        {
            case PingOutcome.Unreachable:
                // The upstream Subsonic server could not be reached. Report this distinctly
                // from bad credentials - the client's password may be perfectly valid.
                _logger.LogWarning("Authentication failed: Subsonic server unreachable for user {User} on {Path} (upstream down, not a credential problem)", username, path);
                await WriteSubsonicError(context, httpStatus: 503, code: 1000, "Subsonic server unreachable - please try again shortly");
                return;
            
            case PingOutcome.UpstreamError:
                _logger.LogWarning("Authentication failed: Subsonic server returned an error for user {User} on {Path}", username, path);
                await WriteSubsonicError(context, httpStatus: 503, code: 1000, "Subsonic server error - please try again shortly");
                return;
            
            case PingOutcome.InvalidCredentials:
                _logger.LogWarning("Authentication failed: invalid credentials for user {User} on {Path}", username, path);
                await WriteSubsonicError(context, httpStatus: 401, code: 40, "Wrong username or password");
                return;
            
            default:
                await _next(context);
                return;
        }
        
        // Cache the validated credentials
        _validatedCredentials[credentialHash] = DateTime.UtcNow.Add(CredentialCacheDuration);
        
        // Clean up expired entries periodically (simple cleanup on every successful auth)
        CleanupExpiredCredentials();
        
        await _next(context);
    }

    private bool IsPublicPath(string path)
    {
        // Check exact matches
        if (PublicPaths.Contains(path))
            return true;
        
        // Check prefix matches for swagger
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
            return true;
        
        return false;
    }

    private string ComputeCredentialHash(Dictionary<string, string> parameters)
    {
        var u = parameters.GetValueOrDefault("u", "");
        var p = parameters.GetValueOrDefault("p", "");
        var t = parameters.GetValueOrDefault("t", "");
        var s = parameters.GetValueOrDefault("s", "");
        
        var combined = $"{u}:{p}:{t}:{s}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    private async Task<PingOutcome> ValidateCredentialsAsync(Dictionary<string, string> parameters)
    {
        // Negative cache: if we recently confirmed the upstream is down, don't fire another
        // (slow) ping per request - a client request storm would otherwise pile up on a dead server.
        if (DateTime.UtcNow < _upstreamDownUntil)
        {
            _logger.LogDebug("Subsonic upstream marked down until {Until:HH:mm:ss} - rejecting without ping", _upstreamDownUntil);
            return PingOutcome.Unreachable;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            
            // Build the authentication parameters for the ping request
            var authParams = new List<string>
            {
                $"u={Uri.EscapeDataString(parameters.GetValueOrDefault("u", ""))}",
                $"c={Uri.EscapeDataString(parameters.GetValueOrDefault("c", "octo-fiesta"))}",
                $"v={Uri.EscapeDataString(parameters.GetValueOrDefault("v", "1.16.1"))}",
                "f=json"
            };
            
            // Add password or token-based auth
            if (parameters.TryGetValue("p", out var password) && !string.IsNullOrEmpty(password))
            {
                authParams.Add($"p={Uri.EscapeDataString(password)}");
            }
            else
            {
                authParams.Add($"t={Uri.EscapeDataString(parameters.GetValueOrDefault("t", ""))}");
                authParams.Add($"s={Uri.EscapeDataString(parameters.GetValueOrDefault("s", ""))}");
            }
            
            var queryString = string.Join("&", authParams);
            var url = $"{_subsonicSettings.Url}/rest/ping?{queryString}";
            
            using var cts = new CancellationTokenSource(PingTimeout);
            var response = await httpClient.GetAsync(url, cts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                // The server answered but not with a 2xx - it's up, just erroring.
                _logger.LogWarning("Subsonic upstream returned HTTP {StatusCode} during auth ping", (int)response.StatusCode);
                return PingOutcome.UpstreamError;
            }
            
            var content = await response.Content.ReadAsStringAsync();
            
            // Check for Subsonic error response
            // JSON format: {"subsonic-response":{"status":"ok",...}} or {"subsonic-response":{"status":"failed","error":{"code":40,...}}}
            if (content.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("status=\"ok\"", StringComparison.OrdinalIgnoreCase))
            {
                return PingOutcome.Valid;
            }
            
            // Server up and answering, but rejected the credentials (code 40/41/70, etc.)
            return PingOutcome.InvalidCredentials;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Subsonic server timed out during auth ping (no response within {Timeout}s)", (int)PingTimeout.TotalSeconds);
            _upstreamDownUntil = DateTime.UtcNow.Add(UnreachableCacheDuration);
            return PingOutcome.Unreachable;
        }
        catch (HttpRequestException ex)
        {
            // Connection refused / DNS failure - the Subsonic server is down.
            _logger.LogWarning("Subsonic server unreachable during auth ping: {Detail}", ex.Message);
            _upstreamDownUntil = DateTime.UtcNow.Add(UnreachableCacheDuration);
            return PingOutcome.Unreachable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error validating credentials against Subsonic server");
            _upstreamDownUntil = DateTime.UtcNow.Add(UnreachableCacheDuration);
            return PingOutcome.Unreachable;
        }
    }

    private async Task WriteSubsonicError(HttpContext context, int httpStatus, int code, string message)
    {
        var format = context.Request.Query["f"].FirstOrDefault() ?? "xml";
        
        context.Response.StatusCode = httpStatus;
        
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            var json = $$"""
                {
                    "subsonic-response": {
                        "status": "failed",
                        "version": "1.16.1",
                        "error": {
                            "code": {{code}},
                            "message": "{{message}}"
                        }
                    }
                }
                """;
            await context.Response.WriteAsync(json);
        }
        else
        {
            context.Response.ContentType = "application/xml; charset=utf-8";
            var xml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <subsonic-response xmlns="http://subsonic.org/restapi" status="failed" version="1.16.1">
                    <error code="{code}" message="{message}"/>
                </subsonic-response>
                """;
            await context.Response.WriteAsync(xml);
        }
    }

    private void CleanupExpiredCredentials()
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _validatedCredentials
            .Where(kv => kv.Value < now)
            .Select(kv => kv.Key)
            .ToList();
        
        foreach (var key in expiredKeys)
        {
            _validatedCredentials.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// Extension methods for registering the Subsonic authentication middleware.
/// </summary>
public static class SubsonicAuthenticationMiddlewareExtensions
{
    public static IApplicationBuilder UseSubsonicAuthentication(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SubsonicAuthenticationMiddleware>();
    }
}
