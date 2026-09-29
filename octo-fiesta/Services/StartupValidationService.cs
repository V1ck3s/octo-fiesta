using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Qobuz;

namespace octo_fiesta.Services;

/// <summary>
/// Hosted service that validates configuration at startup and logs the results.
/// Checks connectivity to Subsonic server and validates music service credentials (Deezer or Qobuz).
/// Uses a dedicated HttpClient without logging to keep console output clean.
/// </summary>
public class StartupValidationService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<SubsonicSettings> _subsonicSettings;
    private readonly IOptions<DeezerSettings> _deezerSettings;
    private readonly IOptions<QobuzSettings> _qobuzSettings;
    private readonly HttpClient _httpClient;

    public StartupValidationService(
        IConfiguration configuration,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings)
    {
        _configuration = configuration;
        _subsonicSettings = subsonicSettings;
        _deezerSettings = deezerSettings;
        _qobuzSettings = qobuzSettings;
        // Create a dedicated HttpClient without logging to keep startup output clean
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("       octo-fiesta starting up...       ");
        Console.WriteLine("========================================");
        Console.WriteLine();

        await ValidateSubsonicAsync(cancellationToken);
        
        // Validate music service credentials based on configured service
        var musicService = _subsonicSettings.Value.MusicService;
        if (musicService == MusicService.Qobuz)
        {
            var qobuzValidator = new QobuzStartupValidator(_qobuzSettings, _httpClient);
            await qobuzValidator.ValidateAsync(cancellationToken);
        }
        else
        {
            var deezerValidator = new DeezerStartupValidator(_deezerSettings, _httpClient);
            await deezerValidator.ValidateAsync(cancellationToken);
        }

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("       Startup validation complete      ");
        Console.WriteLine("========================================");
        Console.WriteLine();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task ValidateSubsonicAsync(CancellationToken cancellationToken)
    {
        var subsonicUrl = _subsonicSettings.Value.Url;

        if (string.IsNullOrWhiteSpace(subsonicUrl))
        {
            WriteStatus("Subsonic URL", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Subsonic__Url environment variable");
            return;
        }

        WriteStatus("Subsonic URL", subsonicUrl, ConsoleColor.Cyan);

        try
        {
        var pingUrl = $"{subsonicUrl.TrimEnd('/')}/rest/ping.view?v=1.16.1&c=octo-fiesta&f=json";
            var response = await _httpClient.GetAsync(pingUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                
                if (content.Contains("\"status\":\"ok\"") || content.Contains("status=\"ok\""))
                {
                    WriteStatus("Subsonic server", "OK", ConsoleColor.Green);
                }
                else if (content.Contains("\"status\":\"failed\"") || content.Contains("status=\"failed\""))
                {
                    WriteStatus("Subsonic server", "REACHABLE", ConsoleColor.Yellow);
                    WriteDetail("Authentication may be required for some operations");
                }
                else
                {
                    WriteStatus("Subsonic server", "REACHABLE", ConsoleColor.Yellow);
                    WriteDetail("Unexpected response format");
                }
            }
            else
            {
                WriteStatus("Subsonic server", $"HTTP {(int)response.StatusCode}", ConsoleColor.Red);
            }
        }
        catch (TaskCanceledException)
        {
            WriteStatus("Subsonic server", "TIMEOUT", ConsoleColor.Red);
            WriteDetail("Could not reach server within 10 seconds");
        }
        catch (HttpRequestException ex)
        {
            WriteStatus("Subsonic server", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
        }
        catch (Exception ex)
        {
            WriteStatus("Subsonic server", "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
        }
    }

    private static void WriteStatus(string label, string value, ConsoleColor valueColor)
    {
        Console.Write($"  {label}: ");
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = valueColor;
        Console.WriteLine(value);
        Console.ForegroundColor = originalColor;
    }

    private static void WriteDetail(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    -> {message}");
        Console.ForegroundColor = originalColor;
    }
}
