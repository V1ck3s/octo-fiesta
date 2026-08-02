using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.JioSaavn;

public class JioSaavnStartupValidator : BaseStartupValidator
{
    private readonly JioSaavnSettings _settings;
    private readonly JioSaavnApiClient _apiClient;

    public JioSaavnStartupValidator(
        IOptions<JioSaavnSettings> settings,
        JioSaavnApiClient apiClient,
        IHttpClientFactory httpClientFactory)
        : base(httpClientFactory.CreateClient())
    {
        _settings = settings.Value;
        _apiClient = apiClient;
    }

    public override string ServiceName => "JioSaavn";

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var qualityKbps = int.TryParse(_settings.Quality, out var parsedQuality)
            ? Math.Clamp(parsedQuality, 12, 320)
            : 320;
        WriteStatus("Quality", $"{qualityKbps} kbps", ConsoleColor.Cyan);

        try
        {
            var songs = await _apiClient.SearchSongsAsync("test", 1, cancellationToken);
            WriteStatus("JioSaavn API", "REACHABLE", ConsoleColor.Green);
            WriteDetail($"Probe succeeded, returned {songs.Count} result(s)");
            return ValidationResult.Success("JioSaavn validation completed");
        }
        catch (Exception ex)
        {
            WriteStatus("JioSaavn API", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("UNREACHABLE", ex.Message, ConsoleColor.Red);
        }
    }
}
