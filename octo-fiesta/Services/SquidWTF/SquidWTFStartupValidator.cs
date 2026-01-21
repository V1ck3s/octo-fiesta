using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Validates SquidWTF service connectivity at startup
/// </summary>
public class SquidWTFStartupValidator : BaseStartupValidator
{
    private readonly SquidWTFSettings _settings;
    
    // API endpoints
    private const string QobuzBaseUrl = "https://qobuz.squid.wtf";
    private const string TidalBaseUrl = "https://triton.squid.wtf";
    
    // Required headers
    private const string QobuzCountryHeader = "Token-Country";
    private const string QobuzCountryValue = "US";
    private const string TidalClientHeader = "x-client";
    private const string TidalClientValue = "BiniLossless/v3.4";

    public override string ServiceName => "SquidWTF";

    public SquidWTFStartupValidator(IOptions<SquidWTFSettings> settings, HttpClient httpClient)
        : base(httpClient)
    {
        _settings = settings.Value;
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var source = _settings.Source ?? "Qobuz";
        var quality = _settings.Quality;
        var isQobuz = source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase);

        WriteStatus("SquidWTF Source", source, ConsoleColor.Cyan);
        
        var qualityDisplay = string.IsNullOrWhiteSpace(quality) 
            ? "auto (highest available)" 
            : quality;
        WriteStatus("SquidWTF Quality", qualityDisplay, ConsoleColor.Cyan);

        // Test connectivity
        try
        {
            if (isQobuz)
            {
                await ValidateQobuzAsync(cancellationToken);
            }
            else
            {
                await ValidateTidalAsync(cancellationToken);
            }

            return ValidationResult.Success("SquidWTF validation completed");
        }
        catch (TaskCanceledException)
        {
            WriteStatus("SquidWTF API", "TIMEOUT", ConsoleColor.Yellow);
            WriteDetail("Could not reach service within timeout period");
            return ValidationResult.Failure("TIMEOUT", "Service unreachable", ConsoleColor.Yellow);
        }
        catch (HttpRequestException ex)
        {
            WriteStatus("SquidWTF API", "UNREACHABLE", ConsoleColor.Yellow);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("UNREACHABLE", ex.Message, ConsoleColor.Yellow);
        }
        catch (Exception ex)
        {
            WriteStatus("SquidWTF API", "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("ERROR", ex.Message, ConsoleColor.Red);
        }
    }

    private async Task ValidateQobuzAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, 
            $"{QobuzBaseUrl}/api/get-music?q=test&offset=0");
        request.Headers.Add(QobuzCountryHeader, QobuzCountryValue);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            WriteStatus("SquidWTF Qobuz API", "AVAILABLE", ConsoleColor.Green);
            WriteDetail("Service is responding normally");
        }
        else
        {
            WriteStatus("SquidWTF Qobuz API", $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
            WriteDetail("Service returned an error status");
        }
    }

    private async Task ValidateTidalAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, 
            $"{TidalBaseUrl}/search/?s=test");
        request.Headers.Add(TidalClientHeader, TidalClientValue);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            WriteStatus("SquidWTF Tidal API", "AVAILABLE", ConsoleColor.Green);
            WriteDetail("Service is responding normally");
        }
        else
        {
            WriteStatus("SquidWTF Tidal API", $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
            WriteDetail("Service returned an error status");
        }
    }
}
