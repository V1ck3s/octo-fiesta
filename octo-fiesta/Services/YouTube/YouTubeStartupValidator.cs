using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.YouTube;

public class YouTubeStartupValidator : BaseStartupValidator
{
    private readonly YouTubeSettings _settings;
    private readonly IYtDlpProcessRunner _processRunner;

    public YouTubeStartupValidator(IOptions<YouTubeSettings> settings, IHttpClientFactory httpClientFactory, IYtDlpProcessRunner processRunner)
        : base(httpClientFactory.CreateClient())
    {
        _settings = settings.Value;
        _processRunner = processRunner;
    }

    public override string ServiceName => "YouTube";

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            WriteStatus("Provider Enabled", "NO", ConsoleColor.Yellow);
            WriteDetail("Set YouTube__Enabled=true to enable YouTube provider");
            return ValidationResult.Success("YouTube provider disabled by configuration");
        }

        WriteStatus("Provider Enabled", "YES", ConsoleColor.Cyan);
        WriteStatus("yt-dlp path", _settings.YtDlpPath, ConsoleColor.Cyan);

        if (_settings.MaxResults <= 0)
        {
            WriteStatus("Max results", "INVALID", ConsoleColor.Red);
            WriteDetail("Set YouTube__MaxResults to a positive integer");
            return ValidationResult.NotConfigured("Invalid YouTube__MaxResults value");
        }

        WriteStatus("Max results", _settings.MaxResults.ToString(), ConsoleColor.Cyan);
        WriteStatus("Audio format", _settings.AudioFormat, ConsoleColor.Cyan);

        if (!string.IsNullOrWhiteSpace(_settings.CookiesPath) && !File.Exists(_settings.CookiesPath))
        {
            WriteStatus("Cookies file", "NOT FOUND", ConsoleColor.Red);
            WriteDetail($"Configured cookies file does not exist: {_settings.CookiesPath}");
            return ValidationResult.NotConfigured("YouTube cookies file not found");
        }

        try
        {
            var result = await _processRunner.ExecuteAsync(_settings.YtDlpPath, ["--version"], cancellationToken);
            if (result.ExitCode != 0)
            {
                WriteStatus("yt-dlp", "UNAVAILABLE", ConsoleColor.Red);
                WriteDetail(result.StandardError);
                return ValidationResult.NotConfigured("yt-dlp is not available");
            }

            WriteStatus("yt-dlp", $"OK ({result.StandardOutput.Trim()})", ConsoleColor.Green);
            return ValidationResult.Success("YouTube validation completed");
        }
        catch (Exception ex)
        {
            WriteStatus("yt-dlp", "UNAVAILABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.NotConfigured("yt-dlp is not available");
        }
    }
}
