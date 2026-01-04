using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models;

namespace octo_fiesta.Services;

/// <summary>
/// Hosted service that validates configuration at startup and logs the results.
/// Checks connectivity to Subsonic server and validates Deezer ARL token.
/// Uses a dedicated HttpClient without logging to keep console output clean.
/// </summary>
public class StartupValidationService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<SubsonicSettings> _subsonicSettings;
    private readonly HttpClient _httpClient;

    public StartupValidationService(
        IConfiguration configuration,
        IOptions<SubsonicSettings> subsonicSettings)
    {
        _configuration = configuration;
        _subsonicSettings = subsonicSettings;
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
        await ValidateDeezerArlAsync(cancellationToken);

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

    private async Task ValidateDeezerArlAsync(CancellationToken cancellationToken)
    {
        var arl = _configuration["Deezer:Arl"];
        var arlFallback = _configuration["Deezer:ArlFallback"];
        var quality = _configuration["Deezer:Quality"];

        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(arl))
        {
            WriteStatus("Deezer ARL", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Deezer__Arl environment variable");
            return;
        }

        WriteStatus("Deezer ARL", MaskSecret(arl), ConsoleColor.Cyan);
        
        if (!string.IsNullOrWhiteSpace(arlFallback))
        {
            WriteStatus("Deezer ARL Fallback", MaskSecret(arlFallback), ConsoleColor.Cyan);
        }

        WriteStatus("Deezer Quality", string.IsNullOrWhiteSpace(quality) ? "auto (highest available)" : quality, ConsoleColor.Cyan);

        // Validate ARL by calling Deezer API
        await ValidateArlTokenAsync(arl, "primary", cancellationToken);
        
        if (!string.IsNullOrWhiteSpace(arlFallback))
        {
            await ValidateArlTokenAsync(arlFallback, "fallback", cancellationToken);
        }
    }

    private async Task ValidateArlTokenAsync(string arl, string label, CancellationToken cancellationToken)
    {
        var fieldName = $"Deezer ARL ({label})";
        
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://www.deezer.com/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null");
            
            request.Headers.Add("Cookie", $"arl={arl}");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                WriteStatus(fieldName, $"HTTP {(int)response.StatusCode}", ConsoleColor.Red);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("results", out var results) &&
                results.TryGetProperty("USER", out var user))
            {
                if (user.TryGetProperty("USER_ID", out var userId))
                {
                    var userIdValue = userId.ValueKind == JsonValueKind.Number 
                        ? userId.GetInt64() 
                        : long.TryParse(userId.GetString(), out var parsed) ? parsed : 0;

                    if (userIdValue > 0)
                    {
                        // BLOG_NAME is the username displayed on Deezer
                        var userName = user.TryGetProperty("BLOG_NAME", out var blogName) && blogName.GetString() is string bn && !string.IsNullOrEmpty(bn)
                            ? bn
                            : user.TryGetProperty("NAME", out var name) && name.GetString() is string n && !string.IsNullOrEmpty(n)
                                ? n
                                : "Unknown";
                        
                        var offerName = GetOfferName(user);
                        
                        WriteStatus(fieldName, "VALID", ConsoleColor.Green);
                        WriteDetail($"Logged in as {userName} ({offerName})");
                        return;
                    }
                }
                
                WriteStatus(fieldName, "INVALID", ConsoleColor.Red);
                WriteDetail("Token is expired or invalid");
            }
            else
            {
                WriteStatus(fieldName, "INVALID", ConsoleColor.Red);
                WriteDetail("Unexpected response from Deezer");
            }
        }
        catch (TaskCanceledException)
        {
            WriteStatus(fieldName, "TIMEOUT", ConsoleColor.Yellow);
            WriteDetail("Could not reach Deezer within 10 seconds");
        }
        catch (HttpRequestException ex)
        {
            WriteStatus(fieldName, "UNREACHABLE", ConsoleColor.Yellow);
            WriteDetail(ex.Message);
        }
        catch (Exception ex)
        {
            WriteStatus(fieldName, "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
        }
    }

    private static string GetOfferName(JsonElement user)
    {
        if (!user.TryGetProperty("OPTIONS", out var options))
        {
            return "Free";
        }

        // Check actual streaming capabilities, not just license_token presence
        var hasLossless = options.TryGetProperty("web_lossless", out var webLossless) && webLossless.GetBoolean();
        var hasHq = options.TryGetProperty("web_hq", out var webHq) && webHq.GetBoolean();

        if (hasLossless)
        {
            return "Premium+ (Lossless)";
        }
        
        if (hasHq)
        {
            return "Premium (HQ)";
        }
        
        return "Free";
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

    /// <summary>
    /// Masks a secret string, showing only the first 4 characters followed by asterisks.
    /// </summary>
    private static string MaskSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "(empty)";
        }

        const int visibleChars = 4;
        if (secret.Length <= visibleChars)
        {
            return new string('*', secret.Length);
        }

        return secret[..visibleChars] + new string('*', Math.Min(secret.Length - visibleChars, 8));
    }
}
