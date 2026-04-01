using Microsoft.AspNetCore.WebUtilities;
using System.Text.Json;

namespace octo_fiesta.Services.Subsonic;

/// <summary>
/// Service responsible for parsing HTTP request parameters from various sources
/// (query string, form body, JSON body) for Subsonic API requests.
/// </summary>
public class SubsonicRequestParser
{
    /// <summary>
    /// Extracts all parameters from an HTTP request (query parameters + body parameters).
    /// Supports multiple content types: application/x-www-form-urlencoded and application/json.
    /// </summary>
    /// <param name="request">The HTTP request to parse</param>
    /// <returns>Dictionary containing all extracted parameters</returns>
    public async Task<Dictionary<string, string>> ExtractAllParametersAsync(HttpRequest request)
    {
        var parameters = new Dictionary<string, string>();

        // Get query parameters
        foreach (var query in request.Query)
        {
            parameters[query.Key] = query.Value.ToString();
        }

        // Get body parameters
        if (request.ContentLength > 0 || request.ContentType != null)
        {
            // Handle application/x-www-form-urlencoded (OpenSubsonic formPost extension)
            if (request.HasFormContentType)
            {
                await ExtractFormParametersAsync(request, parameters);
            }
            // Handle application/json
            else if (request.ContentType?.Contains("application/json") == true)
            {
                await ExtractJsonParametersAsync(request, parameters);
            }
        }

        return parameters;
    }

    /// <summary>
    /// Extracts parameters from form-encoded request body.
    /// </summary>
    private async Task ExtractFormParametersAsync(HttpRequest request, Dictionary<string, string> parameters)
    {
        try
        {
            var form = await request.ReadFormAsync();
            foreach (var field in form)
            {
                parameters[field.Key] = field.Value.ToString();
            }
        }
        catch
        {
            // Fall back to manual parsing if ReadFormAsync fails
            request.EnableBuffering();
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            request.Body.Position = 0;
            
            if (!string.IsNullOrEmpty(body))
            {
                var formParams = QueryHelpers.ParseQuery(body);
                foreach (var param in formParams)
                {
                    parameters[param.Key] = param.Value.ToString();
                }
            }
        }
    }

    /// <summary>
    /// Extracts parameters from JSON request body.
    /// </summary>
    private async Task ExtractJsonParametersAsync(HttpRequest request, Dictionary<string, string> parameters)
    {
        // Keep the request body stream open for downstream proxy forwarding.
        request.EnableBuffering();
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }
        
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var bodyParams = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
                if (bodyParams != null)
                {
                    foreach (var param in bodyParams)
                    {
                        parameters[param.Key] = param.Value?.ToString() ?? "";
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore JSON parsing errors
            }
        }
    }
}
