using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.GDStudio;

class GDStudioStartupValidator : BaseStartupValidator
{
    private readonly GDStudioSettings _settings;

    public GDStudioStartupValidator(IOptions<GDStudioSettings> settings, IHttpClientFactory httpClientFactory)
        : base(httpClientFactory.CreateClient(GDStudioHttpClientConfiguration.ClientName))
    {
        _settings = settings.Value;
    }

    public override string ServiceName => "GDStudio";

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        WriteStatus("Source", _settings.Source, ConsoleColor.Cyan);
        WriteStatus("Api", _settings.Api, ConsoleColor.Cyan);
        if (!GDStudioSettings.ValidBr.Contains(_settings.Br))
        {
            WriteStatus("Br", "INVALID", ConsoleColor.Red);
            WriteDetail($"GDStudio__br must be one of {string.Join(", ", GDStudioSettings.ValidBr)}");
            WriteStatus("GDStudio Service Validation", "FAILED", ConsoleColor.Red);
            return ValidationResult.NotConfigured("Invalid br setting");
        }
        WriteStatus("Br", _settings.Br.ToString(), ConsoleColor.Cyan);
        WriteStatus("Proxy", string.IsNullOrWhiteSpace(_settings.Proxy) ? "none" : _settings.Proxy, ConsoleColor.Cyan);
        try
        {
            using var r = await _httpClient.GetAsync(
                _settings.Url($"types=search&source={Uri.EscapeDataString(_settings.Sources[0])}&name=a&count=1"), cancellationToken);
            if (!r.IsSuccessStatusCode)
            {
                WriteStatus("GDStudio Service Validation", "FAILED", ConsoleColor.Red);
                return ValidationResult.Failure("API unreachable", $"GDStudio API returned {r.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            var result = HandleException(ex, "GDStudio API");
            WriteValidationResult("GDStudio API", result);
            return result;
        }
        WriteStatus("GDStudio Service Validation", "SUCCESS", ConsoleColor.Green);
        return ValidationResult.Success("GDStudio API reachable");
    }
}
