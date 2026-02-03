using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;
using octo_fiesta.Services;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Validates SquidWTF service connectivity at startup (no auth needed)
/// </summary>
public class SquidWTFStartupValidator : BaseStartupValidator
{
    private const string DefaultTidalBaseUrl = "https://triton.squid.wtf";
    private readonly SquidWTFSettings _settings;
    private readonly InstanceManager _instanceManager;

    public override string ServiceName => "SquidWTF";

    public SquidWTFStartupValidator(IOptions<SquidWTFSettings> settings, HttpClient httpClient, InstanceManager instanceManager)
        : base(httpClient)
    {
        _settings = settings.Value;
        _instanceManager = instanceManager;
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();

        var quality = _settings.Quality?.ToUpperInvariant() switch
        {
            "FLAC" => "LOSSLESS",
            "HI_RES" => "HI_RES_LOSSLESS",
            "LOSSLESS" => "LOSSLESS",
            "HIGH" => "HIGH",
            "LOW" => "LOW",
            _ => "LOSSLESS (default)"
        };

        WriteStatus("SquidWTF Quality", quality, ConsoleColor.Cyan);

        // Resolve a streaming instance using configured defaults to avoid triggering InstanceManager info logs
        string baseUrl = DefaultTidalBaseUrl;
        try
        {
            var groups = _instanceManager.GetDefaultInstances();
            var url = groups.Streaming.FirstOrDefault();
            if (!string.IsNullOrEmpty(url))
            {
                baseUrl = url.TrimEnd('/');
            }
        }
        catch (Exception ex)
        {
            // Log debug but continue with default
            WriteDetail($"Could not read default instances: {ex.Message} - falling back to {DefaultTidalBaseUrl}");
        }

        try
        {
            var response = await _httpClient.GetAsync(baseUrl + "/", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                WriteStatus("SquidWTF API", "REACHABLE", ConsoleColor.Green);
                WriteDetail($"Using instance: {baseUrl}");

                // Try a test search to verify functionality
                await ValidateSearchFunctionality(baseUrl, cancellationToken);

                return ValidationResult.Success("SquidWTF validation completed");
            }
            else
            {
                WriteStatus("SquidWTF API", $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
                WriteDetail($"Service may be temporarily unavailable at {baseUrl}");
                return ValidationResult.Failure($"{response.StatusCode}", $"SquidWTF returned code from {baseUrl}");
            }
        }
        catch (TaskCanceledException)
        {
            WriteStatus("SquidWTF API", "TIMEOUT", ConsoleColor.Yellow);
            WriteDetail($"Could not reach service at {baseUrl} within timeout period");
            return ValidationResult.Failure("-1", "SquidWTF connection timeout");
        }
        catch (HttpRequestException ex)
        {
            WriteStatus("SquidWTF API", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail($"{ex.Message} (instance: {baseUrl})");
            return ValidationResult.Failure("-1", $"Cannot connect to SquidWTF at {baseUrl}: {ex.Message}");
        }
        catch (Exception ex)
        {
            WriteStatus("SquidWTF API", "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("-1", $"Validation error: {ex.Message}");
        }
    }

    private async Task ValidateSearchFunctionality(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            // Test search with a simple query
            var searchUrl = $"{baseUrl}/search/?s=Taylor%20Swift";
            var searchResponse = await _httpClient.GetAsync(searchUrl, cancellationToken);

            if (searchResponse.IsSuccessStatusCode)
            {
                var json = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("items", out var items))
                {
                    var itemCount = items.GetArrayLength();
                    WriteStatus("Search Functionality", "WORKING", ConsoleColor.Green);
                    WriteDetail($"Test search returned {itemCount} results (instance: {baseUrl})");
                }
                else
                {
                    WriteStatus("Search Functionality", "UNEXPECTED RESPONSE", ConsoleColor.Yellow);
                    WriteDetail($"Unexpected search response from {baseUrl}");
                }
            }
            else
            {
                WriteStatus("Search Functionality", $"HTTP {(int)searchResponse.StatusCode}", ConsoleColor.Yellow);
                WriteDetail($"Search failed at {baseUrl}");
            }
        }
        catch (Exception ex)
        {
            WriteStatus("Search Functionality", "ERROR", ConsoleColor.Yellow);
            WriteDetail($"Could not verify search at {baseUrl}: {ex.Message}");
        }
    }
}
