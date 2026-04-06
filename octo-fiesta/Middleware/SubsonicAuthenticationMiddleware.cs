using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Middleware;

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
            await WriteSubsonicError(context, 40, "Missing authentication parameters");
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
        var isValid = await ValidateCredentialsAsync(parameters);
        
        if (!isValid)
        {
            _logger.LogWarning("Authentication failed: invalid credentials for user {User} on {Path}", username, path);
            await WriteSubsonicError(context, 40, "Wrong username or password");
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

    private async Task<bool> ValidateCredentialsAsync(Dictionary<string, string> parameters)
    {
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
            
            var response = await httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            
            var content = await response.Content.ReadAsStringAsync();
            
            // Check for Subsonic error response
            // JSON format: {"subsonic-response":{"status":"ok",...}} or {"subsonic-response":{"status":"failed","error":{"code":40,...}}}
            if (content.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            // XML format: <subsonic-response status="ok" ...> or <subsonic-response status="failed" ...>
            if (content.Contains("status=\"ok\"", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating credentials against Subsonic server");
            // On connection error, deny access for security
            return false;
        }
    }

    private async Task WriteSubsonicError(HttpContext context, int code, string message)
    {
        var format = context.Request.Query["f"].FirstOrDefault() ?? "xml";
        
        context.Response.StatusCode = 401;
        
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
