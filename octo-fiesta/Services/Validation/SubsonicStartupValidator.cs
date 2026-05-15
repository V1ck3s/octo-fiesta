using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.Validation;

/// <summary>
/// Validates Subsonic server connectivity at startup
/// </summary>
public class SubsonicStartupValidator : BaseStartupValidator
{
    private readonly IOptions<SubsonicSettings> _subsonicSettings;

    public override string ServiceName => "Subsonic";

    public SubsonicStartupValidator(IOptions<SubsonicSettings> subsonicSettings, HttpClient httpClient)
        : base(httpClient)
    {
        _subsonicSettings = subsonicSettings;
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var subsonicUrl = _subsonicSettings.Value.Url;

        if (string.IsNullOrWhiteSpace(subsonicUrl))
        {
            WriteStatus("Subsonic URL", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Subsonic__Url environment variable");
            return ValidationResult.NotConfigured("Subsonic URL not configured");
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
                    await ValidateAdminAccountAsync(subsonicUrl, cancellationToken);
                    return ValidationResult.Success("Subsonic server is accessible");
                }
                else if (content.Contains("\"status\":\"failed\"") || content.Contains("status=\"failed\""))
                {
                    WriteStatus("Subsonic server", "REACHABLE", ConsoleColor.Yellow);
                    WriteDetail("Authentication may be required for some operations");
                    await ValidateAdminAccountAsync(subsonicUrl, cancellationToken);
                    return ValidationResult.Success("Subsonic server is reachable");
                }
                else
                {
                    WriteStatus("Subsonic server", "REACHABLE", ConsoleColor.Yellow);
                    WriteDetail("Unexpected response format");
                    return ValidationResult.Success("Subsonic server is reachable");
                }
            }
            else
            {
                WriteStatus("Subsonic server", $"HTTP {(int)response.StatusCode}", ConsoleColor.Red);
                return ValidationResult.Failure($"HTTP {(int)response.StatusCode}", 
                    "Subsonic server returned an error", ConsoleColor.Red);
            }
        }
        catch (TaskCanceledException)
        {
            WriteStatus("Subsonic server", "TIMEOUT", ConsoleColor.Red);
            WriteDetail("Could not reach server within 10 seconds");
            return ValidationResult.Failure("TIMEOUT", "Could not reach server within timeout period", ConsoleColor.Red);
        }
        catch (HttpRequestException ex)
        {
            WriteStatus("Subsonic server", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("UNREACHABLE", ex.Message, ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            WriteStatus("Subsonic server", "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("ERROR", ex.Message, ConsoleColor.Red);
        }
    }

    private async Task ValidateAdminAccountAsync(
        string subsonicUrl,
        CancellationToken cancellationToken
    )
    {
        var adminUsername = _subsonicSettings.Value.AdminUsername;
        var adminPassword = _subsonicSettings.Value.AdminPassword;

        if (string.IsNullOrWhiteSpace(adminUsername) && string.IsNullOrWhiteSpace(adminPassword))
        {
            WriteStatus("Admin account", "NOT CONFIGURED", ConsoleColor.DarkGray);
            WriteDetail("Optional - set Subsonic__AdminUsername and Subsonic__AdminPassword to avoid problems with admin required actions such as library scan");
            return;
        }
        else if (string.IsNullOrWhiteSpace(adminUsername))
        {
            WriteStatus("Admin account", "PARTIAL CONFIG", ConsoleColor.Yellow);
            WriteDetail("Subsonic__AdminUsername is missing - To use the admin account feature Subsonic__AdminUsername and Subsonic__AdminPassword are required.");
            return;
        }
        else if (string.IsNullOrWhiteSpace(adminPassword))
        {
            WriteStatus("Admin account", "PARTIAL CONFIG", ConsoleColor.Yellow);
            WriteDetail("Subsonic__AdminPassword is missing - To use the admin account feature Subsonic__AdminUsername and Subsonic__AdminPassword are required.");
            return;
        }

        var adminCredentialsParameters = new Dictionary<string, string>
        {
            ["u"] = adminUsername,
            ["p"] = adminPassword,
            ["v"] = "1.16.1",
            ["c"] = "octo-fiesta"
        };
        
        var credentials = SubsonicCredentials.TryFromDictionary(adminCredentialsParameters);

        if (credentials == null)
        {
            WriteStatus("Admin account", "ERROR", ConsoleColor.Red);
            WriteDetail("Failed to build credentials from admin account configuration");
            return;
        }

        var requestUrl = $"{subsonicUrl.TrimEnd('/')}/rest/getUser?f=json"
        + $"&u={Uri.EscapeDataString(credentials.Username)}"
        + $"&t={Uri.EscapeDataString(credentials.Token!)}"
        + $"&s={Uri.EscapeDataString(credentials.Salt!)}"
        + $"&v={Uri.EscapeDataString(credentials.ApiVersion)}"
        + $"&c={Uri.EscapeDataString(credentials.ClientName)}"
        + $"&username={Uri.EscapeDataString(credentials.Username)}";

        try
        {
            var response = await _httpClient.GetAsync(requestUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                WriteStatus("Admin account", $"HTTP {(int)response.StatusCode}", ConsoleColor.Red);
                return;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement.GetProperty("subsonic-response");

            if (root.TryGetProperty("status", out var statusObj) && statusObj.GetString() == "failed")
            {
                HandleSubsonicError(root, adminUsername);
                return;
            }

            if (root.TryGetProperty("user", out var userObj) && userObj.TryGetProperty("adminRole", out var adminRoleField))
            {
                if (adminRoleField.GetBoolean())
                {
                    WriteStatus("Admin account", "OK", ConsoleColor.Green);
                    WriteDetail($"User '{adminUsername}' authenticated with admin rights");
                }
                else
                {
                    WriteStatus("Admin account", "NOT ADMIN", ConsoleColor.Yellow);
                    WriteDetail($"User '{adminUsername}' authenticated but has no admin rights - this may cause problems with actions that require admin permissions e.g. library scans");
                }
            }
            else
            {
                WriteStatus("Admin account", "UNEXPECTED RESPONSE", ConsoleColor.Yellow);
                WriteDetail("Could not find user.adminRole in response from Navidrome");
            }

        }
        catch (TaskCanceledException)
        {
            WriteStatus("Admin account", "TIMEOUT", ConsoleColor.Red);
            WriteDetail("Admin verification did not complete within 10 seconds");
        }
        catch (HttpRequestException ex)
        {
            WriteStatus("Admin account", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
        }
        catch (Exception ex)
        {
            WriteStatus("Admin account", "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
        }
    }

    private static void HandleSubsonicError(
        JsonElement root,
        string username)
    {
        var errorCode = 0;
        var errorMsg = "Unknown error";

        if (root.TryGetProperty("error", out var errorObj))
        {
            if (errorObj.TryGetProperty("code", out var errorCodeField)) errorCode = errorCodeField.GetInt32();
            if (errorObj.TryGetProperty("message", out var errorMsgField)) errorMsg = errorMsgField.GetString();
        }

        switch (errorCode)
        {
            case 40:
                WriteStatus("Admin account", "AUTH FAILED", ConsoleColor.Red);
                WriteDetail($"Navidrome rejected credentials for '{username}' (wrong username or password)");
                break;

            case 41:
                WriteStatus("Admin account", "TOKEN AUTH UNSUPPORTED", ConsoleColor.Red);
                WriteDetail("Server does not support token-based auth - check Navidrome version");
                break;

            case 50:
                WriteStatus("Admin account", "NOT AUTHORIZED", ConsoleColor.Yellow);
                WriteDetail($"User '{username}' is not authorized to query user details");
                break;

            case 70:
                WriteStatus("Admin account", "USER NOT FOUND", ConsoleColor.Red);
                WriteDetail($"Navidrome could not find user '{username}'");
                break;

            default:
                WriteStatus("Admin account", "ERROR", ConsoleColor.Red);
                WriteDetail($"Code: {errorCode}:{errorMsg}");
                break;
        }
    }
}
