using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Validates SquidWTF service connectivity at startup (no auth needed)
/// </summary>
public class SquidWTFStartupValidator : BaseStartupValidator
{
    private readonly SquidWTFSettings _settings;

    public override string ServiceName => "SquidWTF";

    public SquidWTFStartupValidator(IOptions<SquidWTFSettings> settings, HttpClient httpClient)
        : base(httpClient)
    {
        _settings = settings.Value;
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

        // Test connectivity to triton.squid.wtf
        try
        {
            var response = await _httpClient.GetAsync("https://triton.squid.wtf/", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                WriteStatus("SquidWTF API", "REACHABLE", ConsoleColor.Green);
                WriteDetail("No authentication required - powered by Tidal");
                
                // Try a test search to verify functionality
                await ValidateSearchFunctionality(cancellationToken);
                
                return ValidationResult.Success("SquidWTF validation completed");
            }
            else
            {
                WriteStatus("SquidWTF API", $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
                WriteDetail("Service may be temporarily unavailable");
			return ValidationResult.Failure($"{response.StatusCode}", "SquidWTF returned code");
            }
        }
        catch (TaskCanceledException)
        {
            WriteStatus("SquidWTF API", "TIMEOUT", ConsoleColor.Yellow);
            WriteDetail("Could not reach service within timeout period");
            return ValidationResult.Failure("-1", "SquidWTF connection timeout");
        }
        catch (HttpRequestException ex)
        {
            WriteStatus("SquidWTF API", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("-1", $"Cannot connect to SquidWTF: {ex.Message}");
        }
        catch (Exception ex)
        {
            WriteStatus("SquidWTF API", "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("-1", $"Validation error: {ex.Message}");
        }
    }

    private async Task ValidateSearchFunctionality(CancellationToken cancellationToken)
    {
        try
        {
            // Test search with a simple query
            var searchUrl = "https://triton.squid.wtf/search/?s=Taylor%20Swift";
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
                    WriteDetail($"Test search returned {itemCount} results");
                }
                else
                {
                    WriteStatus("Search Functionality", "UNEXPECTED RESPONSE", ConsoleColor.Yellow);
                }
            }
            else
            {
                WriteStatus("Search Functionality", $"HTTP {(int)searchResponse.StatusCode}", ConsoleColor.Yellow);
            }
        }
        catch (Exception ex)
        {
            WriteStatus("Search Functionality", "ERROR", ConsoleColor.Yellow);
            WriteDetail($"Could not verify search: {ex.Message}");
        }
    }
}