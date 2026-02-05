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
    private readonly SquidWTFInstanceManager? _instanceManager;

    public override string ServiceName => "SquidWTF";

    public SquidWTFStartupValidator(
        IOptions<SquidWTFSettings> settings, 
        HttpClient httpClient,
        IServiceProvider serviceProvider)
        : base(httpClient)
    {
        _settings = settings.Value;
        _instanceManager = serviceProvider.GetService<SquidWTFInstanceManager>();
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();

        var source = _settings.Source ?? "Qobuz";
        var quality = _settings.Quality?.ToUpperInvariant() switch
        {
            "FLAC" => "LOSSLESS",
            "HI_RES" => "HI_RES_LOSSLESS",
            "LOSSLESS" => "LOSSLESS",
            "HIGH" => "HIGH",
            "LOW" => "LOW",
            "27" => "FLAC 24-bit/192kHz",
            "7" => "FLAC 24-bit/96kHz",
            "6" => "FLAC 16-bit",
            "5" => "MP3 320kbps",
            _ => source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase) 
                ? "FLAC 24-bit/192kHz (default)" 
                : "LOSSLESS (default)"
        };

        WriteStatus("SquidWTF Source", source, ConsoleColor.Cyan);
        WriteStatus("SquidWTF Quality", quality, ConsoleColor.Cyan);
        
        if (_settings.InstanceTimeoutSeconds > 0)
        {
            WriteStatus("Instance Timeout", $"{_settings.InstanceTimeoutSeconds}s", ConsoleColor.Cyan);
        }

        try
        {
            if (source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase))
            {
                return await ValidateQobuzAsync(cancellationToken);
            }
            else
            {
                return await ValidateTidalAsync(cancellationToken);
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

    private async Task<ValidationResult> ValidateQobuzAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync("https://qobuz.squid.wtf/api/get-music?q=test&offset=0", cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            WriteStatus("SquidWTF API", "REACHABLE", ConsoleColor.Green);
            WriteDetail("No authentication required - powered by Qobuz");
            return ValidationResult.Success("SquidWTF Qobuz validation completed");
        }
        else
        {
            WriteStatus("SquidWTF API", $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
            WriteDetail("Service may be temporarily unavailable");
            return ValidationResult.Failure($"{response.StatusCode}", "SquidWTF returned code");
        }
    }

    private async Task<ValidationResult> ValidateTidalAsync(CancellationToken cancellationToken)
    {
        if (_instanceManager != null)
        {
            // Use instance manager to test with failover
            var response = await _instanceManager.SendWithFailoverAsync(baseUrl =>
            {
                return new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/search/?s=test");
            }, cancellationToken);

            var currentInstance = _instanceManager.GetCurrentInstance();
            
            if (response.IsSuccessStatusCode)
            {
                WriteStatus("SquidWTF API", "REACHABLE", ConsoleColor.Green);
                WriteStatus("Active Instance", currentInstance ?? "unknown", ConsoleColor.Cyan);
                WriteDetail("No authentication required - powered by Tidal");
                
                // Try a test search to verify functionality
                await ValidateSearchFunctionality(cancellationToken);
                
                return ValidationResult.Success("SquidWTF Tidal validation completed");
            }
            else
            {
                WriteStatus("SquidWTF API", $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
                WriteDetail("Service may be temporarily unavailable");
                return ValidationResult.Failure($"{response.StatusCode}", "SquidWTF returned code");
            }
        }
        else
        {
            // Fallback if instance manager not available
            var response = await _httpClient.GetAsync("https://tidal-api.binimum.org/", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                WriteStatus("SquidWTF API", "REACHABLE", ConsoleColor.Green);
                WriteDetail("No authentication required - powered by Tidal");
                return ValidationResult.Success("SquidWTF Tidal validation completed");
            }
            else
            {
                WriteStatus("SquidWTF API", $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
                WriteDetail("Service may be temporarily unavailable");
                return ValidationResult.Failure($"{response.StatusCode}", "SquidWTF returned code");
            }
        }
    }

    private async Task ValidateSearchFunctionality(CancellationToken cancellationToken)
    {
        try
        {
            if (_instanceManager != null)
            {
                var searchResponse = await _instanceManager.SendWithFailoverAsync(baseUrl =>
                {
                    return new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/search/?s=Taylor%20Swift");
                }, cancellationToken);

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
        }
        catch (Exception ex)
        {
            WriteStatus("Search Functionality", "ERROR", ConsoleColor.Yellow);
            WriteDetail($"Could not verify search: {ex.Message}");
        }
    }
}
