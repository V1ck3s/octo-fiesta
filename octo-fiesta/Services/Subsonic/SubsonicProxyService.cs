using Microsoft.AspNetCore.Mvc;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Subsonic;

/// <summary>
/// Handles proxying requests to the underlying Subsonic server.
/// </summary>
public class SubsonicProxyService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SubsonicProxyService(
        IHttpClientFactory httpClientFactory,
        Microsoft.Extensions.Options.IOptions<SubsonicSettings> subsonicSettings,
        IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Relays a request to the Subsonic server and returns the response.
    /// </summary>
    public async Task<(byte[] Body, string? ContentType)> RelayAsync(
        string endpoint, 
        Dictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(kv => 
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_subsonicSettings.Url}/{endpoint}?{query}";
        
        HttpResponseMessage response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        var body = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();
        
        return (body, contentType);
    }

    /// <summary>
    /// Safely relays a request to the Subsonic server, returning null on failure.
    /// </summary>
    public async Task<(byte[]? Body, string? ContentType, bool Success)> RelaySafeAsync(
        string endpoint, 
        Dictionary<string, string> parameters)
    {
        try
        {
            var result = await RelayAsync(endpoint, parameters);
            return (result.Body, result.ContentType, true);
        }
        catch
        {
            return (null, null, false);
        }
    }

    /// <summary>
    /// Headers that should not be forwarded (hop-by-hop headers).
    /// </summary>
    private static readonly HashSet<string> ExcludedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding",
        "TE", "Trailer", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate",
        "Accept-Encoding" // Don't forward to avoid compressed responses that we'd need to handle
    };

    /// <summary>
    /// Relays a request to the Subsonic server, preserving HTTP method, body, and headers.
    /// This provides true transparent proxying for better client compatibility.
    /// </summary>
    public async Task<(byte[] Body, string? ContentType, int StatusCode)> RelayRequestAsync(
        string endpoint,
        HttpRequest incomingRequest,
        CancellationToken cancellationToken = default)
    {
        // Build URL with query string from original request
        var url = $"{_subsonicSettings.Url}/{endpoint}{incomingRequest.QueryString}";
        
        using var request = new HttpRequestMessage(new HttpMethod(incomingRequest.Method), url);
        
        // Forward headers (excluding hop-by-hop headers)
        foreach (var header in incomingRequest.Headers)
        {
            if (!ExcludedRequestHeaders.Contains(header.Key) && 
                !header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
        
        // Forward body for POST/PUT/PATCH
        if (incomingRequest.Method != "GET" && incomingRequest.Method != "HEAD")
        {
            incomingRequest.EnableBuffering();
            incomingRequest.Body.Position = 0;
            
            using var reader = new StreamReader(incomingRequest.Body, leaveOpen: true);
            var bodyContent = await reader.ReadToEndAsync(cancellationToken);
            incomingRequest.Body.Position = 0;
            
            if (!string.IsNullOrEmpty(bodyContent))
            {
                request.Content = new StringContent(bodyContent);
                
                // Preserve content type
                if (incomingRequest.ContentType != null)
                {
                    request.Content.Headers.ContentType = 
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(incomingRequest.ContentType);
                }
            }
        }
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString();
        
        return (body, contentType, (int)response.StatusCode);
    }

    private static readonly string[] StreamingRequiredHeaders =
    {
        "Accept-Ranges",
        "Content-Range",
        "Content-Length",
        "ETag",
        "Last-Modified"
    };

    /// <summary>
    /// Relays a stream request to the Subsonic server with range processing support.
    /// </summary>
    public async Task<IActionResult> RelayStreamAsync(
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get HTTP context for request/response forwarding
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return new ObjectResult(new { error = "HTTP context not available" })
                {
                    StatusCode = 500
                };
            }
            
            var incomingRequest = httpContext.Request;
            var outgoingResponse = httpContext.Response;

            var query = string.Join("&", parameters.Select(kv => 
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            var url = $"{_subsonicSettings.Url}/rest/stream?{query}";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Forward Range headers for progressive streaming support (iOS clients)
            if (incomingRequest.Headers.TryGetValue("Range", out var range))
            {
                request.Headers.TryAddWithoutValidation("Range", range.ToArray());
            }
            
            if (incomingRequest.Headers.TryGetValue("If-Range", out var ifRange))
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange.ToArray());
            }
            
            var response = await _httpClient.SendAsync(
                request, 
                HttpCompletionOption.ResponseHeadersRead, 
                cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                return new StatusCodeResult((int)response.StatusCode);
            }

            // Forward HTTP status code (e.g., 206 Partial Content for range requests)
            outgoingResponse.StatusCode = (int)response.StatusCode;

            // Forward streaming-required headers from upstream response
            foreach (var header in StreamingRequiredHeaders)
            {
                if (response.Headers.TryGetValues(header, out var values) ||
                    response.Content.Headers.TryGetValues(header, out values))
                {
                    outgoingResponse.Headers[header] = values.ToArray();
                }
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
            
            return new FileStreamResult(stream, contentType)
            {
                EnableRangeProcessing = true
            };
        }
        catch (Exception ex)
        {
            return new ObjectResult(new { error = $"Error streaming from Subsonic: {ex.Message}" })
            {
                StatusCode = 500
            };
        }
    }
}
