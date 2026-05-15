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
        var configuredQuality = _settings.Quality;
        var (quality, usedDefaultFallback) = GetEffectiveQualityLabel(source, configuredQuality);

        WriteStatus("SquidWTF Source", source, ConsoleColor.Cyan);
        WriteStatus("SquidWTF Quality", quality, ConsoleColor.Cyan);
        if (usedDefaultFallback && !string.IsNullOrWhiteSpace(configuredQuality))
        {
            WriteStatus("SquidWTF Quality Warning", "INCOMPATIBLE CONFIG", ConsoleColor.Yellow);
            WriteDetail($"Quality '{configuredQuality}' is not valid for source '{source}'. Falling back to default quality.");
        }
        
        if (_settings.InstanceTimeoutSeconds > 0)
        {
            WriteStatus("Instance Timeout", $"{_settings.InstanceTimeoutSeconds}s", ConsoleColor.Cyan);
        }

        if (_settings.Instances is { Count: > 0 })
        {
            WriteStatus("Instance Source", $"custom ({_settings.Instances.Count})", ConsoleColor.Cyan);
        }
        else if (!string.IsNullOrWhiteSpace(_settings.InstancesUrl))
        {
            WriteStatus("Instances URL", _settings.InstancesUrl!, ConsoleColor.Cyan);
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
            var response = await _httpClient.GetAsync("https://monochrome-api.samidy.com/", cancellationToken);

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

    private static (string Label, bool UsedDefaultFallback) GetEffectiveQualityLabel(string source, string? configuredQuality)
    {
        var sourceIsQobuz = source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase);
        var quality = configuredQuality?.ToUpperInvariant();

        if (sourceIsQobuz)
        {
            return quality switch
            {
                "FLAC_24_192" or "FLAC_24" or "27" => ("FLAC 24-bit/192kHz", false),
                "FLAC_24_96" or "7" => ("FLAC 24-bit/96kHz", false),
                "FLAC_16" or "FLAC" or "6" => ("FLAC 16-bit", false),
                "MP3_320" or "MP3" or "5" => ("MP3 320kbps", false),
                _ => ("FLAC 24-bit/192kHz (default)", true)
            };
        }

        return quality switch
        {
            "HI_RES_LOSSLESS" or "HI_RES" or "FLAC_24" => ("HI_RES_LOSSLESS", false),
            "LOSSLESS" or "FLAC" or "FLAC_16" => ("LOSSLESS", false),
            "HIGH" or "AAC_320" or "AAC_HIGH" => ("HIGH", false),
            "LOW" or "AAC_96" or "AAC_LOW" => ("LOW", false),
            _ => ("HI_RES_LOSSLESS (default)", true)
        };
    }
}
