using System.Reflection;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Validation;

/// <summary>
/// Orchestrates startup validation for all configured services.
/// This replaces the old StartupValidationService with a more extensible architecture.
/// </summary>
public class StartupValidationOrchestrator : IHostedService
{
    private readonly IEnumerable<IStartupValidator> _validators;
    private readonly IOptions<SubsonicSettings> _subsonicSettings;

    private const int BoxWidth = 62;

    public StartupValidationOrchestrator(
        IEnumerable<IStartupValidator> validators,
        IOptions<SubsonicSettings> subsonicSettings)
    {
        _validators = validators;
        _subsonicSettings = subsonicSettings;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var version = GetVersion();
        var settings = _subsonicSettings.Value;

        Console.WriteLine();
        WriteHeader($"octo-fiesta v{version}");

        // Configuration summary section
        WriteSection("Configuration", () =>
        {
            WriteConfigLine("Music Service", settings.MusicService.ToString());
            WriteConfigLine("Storage Mode", settings.StorageMode.ToString());
            WriteConfigLine("Download Mode", settings.DownloadMode.ToString());
            WriteConfigLine("External Playlists", settings.EnableExternalPlaylists ? "Enabled" : "Disabled");
        });

        // Run all validators
        foreach (var validator in _validators)
        {
            try
            {
                await WriteSectionAsync(validator.ServiceName, () => validator.ValidateAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                WriteError($"Error validating {validator.ServiceName}: {ex.Message}");
            }
        }

        Console.WriteLine();
        WriteSuccess("Startup validation complete");
        Console.WriteLine();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        // Try to get the informational version first (includes pre-release tags like -dev.5+g1a2b3c4)
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(infoVersion))
        {
            // Remove the +commitHash suffix added by SourceLink if present (e.g., "0.1.0-dev+abc123def")
            var plusIndex = infoVersion.IndexOf('+');
            return plusIndex > 0 ? infoVersion[..plusIndex] : infoVersion;
        }
        
        // Fallback to assembly version
        var version = assembly.GetName().Version;
        if (version is null)
        {
            return "unknown";
        }

        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static void WriteHeader(string title)
    {
        var padding = (BoxWidth - 2 - title.Length) / 2;
        var paddingExtra = (BoxWidth - 2 - title.Length) % 2;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"╔{new string('═', BoxWidth - 2)}╗");
        Console.WriteLine($"║{new string(' ', padding)}{title}{new string(' ', padding + paddingExtra)}║");
        Console.WriteLine($"╚{new string('═', BoxWidth - 2)}╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void WriteSection(string title, Action content)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"┌─ {title} {new string('─', BoxWidth - 5 - title.Length)}┐");
        Console.ResetColor();
        
        content();
        
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"└{new string('─', BoxWidth - 2)}┘");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static async Task WriteSectionAsync(string title, Func<Task> content)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"┌─ {title} {new string('─', BoxWidth - 5 - title.Length)}┐");
        Console.ResetColor();
        
        await content();
        
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"└{new string('─', BoxWidth - 2)}┘");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void WriteConfigLine(string label, string value)
    {
        Console.Write($"  {label}: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(value);
        Console.ResetColor();
    }

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ {message}");
        Console.ResetColor();
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ {message}");
        Console.ResetColor();
    }
}
