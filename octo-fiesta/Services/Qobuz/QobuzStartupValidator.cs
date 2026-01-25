using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.Qobuz;

/// <summary>
/// Validates Qobuz credentials at startup
/// </summary>
public class QobuzStartupValidator : BaseStartupValidator
{
    private readonly IOptions<QobuzSettings> _qobuzSettings;

    public override string ServiceName => "Qobuz";

    public QobuzStartupValidator(IOptions<QobuzSettings> qobuzSettings, HttpClient httpClient)
        : base(httpClient)
    {
        _qobuzSettings = qobuzSettings;
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var userAuthToken = _qobuzSettings.Value.UserAuthToken;
        var userId = _qobuzSettings.Value.UserId;
        var quality = _qobuzSettings.Value.Quality;

        if (string.IsNullOrWhiteSpace(userAuthToken))
        {
            WriteStatus("Qobuz UserAuthToken", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Qobuz__UserAuthToken environment variable");
            return ValidationResult.NotConfigured("Qobuz UserAuthToken not configured");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            WriteStatus("Qobuz UserId", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Qobuz__UserId environment variable");
            return ValidationResult.NotConfigured("Qobuz UserId not configured");
        }

        WriteStatus("Qobuz UserAuthToken", MaskSecret(userAuthToken), ConsoleColor.Cyan);
        WriteStatus("Qobuz UserId", userId, ConsoleColor.Cyan);
        WriteStatus("Qobuz Quality", quality ?? "auto (highest available)", ConsoleColor.Cyan);

        // Validate token by calling Qobuz API
        await ValidateQobuzTokenAsync(userAuthToken, userId, cancellationToken);

        return ValidationResult.Success("Qobuz validation completed");
    }

    private async Task ValidateQobuzTokenAsync(string userAuthToken, string userId, CancellationToken cancellationToken)
    {
        const string fieldName = "Qobuz credentials";
        
        try
        {
            // First, get the app ID from bundle service (simple check)
            var bundleUrl = "https://play.qobuz.com/login";
            var bundleResponse = await _httpClient.GetAsync(bundleUrl, cancellationToken);
            
            if (!bundleResponse.IsSuccessStatusCode)
            {
                WriteStatus(fieldName, "UNABLE TO VERIFY", ConsoleColor.Yellow);
                WriteDetail("Could not fetch Qobuz app configuration");
                return;
            }

            // Try to validate with a simple API call
            // We'll use the user favorites endpoint which requires authentication
            var appId = "798273057"; // Fallback app ID
            var apiUrl = $"https://www.qobuz.com/api.json/0.2/favorite/getUserFavorites?user_id={userId}&app_id={appId}";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("X-App-Id", appId);
            request.Headers.Add("X-User-Auth-Token", userAuthToken);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // 401 means invalid token, other errors might be network issues
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    WriteStatus(fieldName, "INVALID", ConsoleColor.Red);
                    WriteDetail("Token is expired or invalid");
                }
                else
                {
                    WriteStatus(fieldName, $"HTTP {(int)response.StatusCode}", ConsoleColor.Yellow);
                    WriteDetail("Unable to verify credentials");
                }
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            
            // If we got a successful response, credentials are valid
            if (!string.IsNullOrEmpty(json) && !json.Contains("\"error\""))
            {
                WriteStatus(fieldName, "VALID", ConsoleColor.Green);
                WriteDetail($"User ID: {userId}");
            }
            else
            {
                WriteStatus(fieldName, "INVALID", ConsoleColor.Red);
                WriteDetail("Unexpected response from Qobuz");
            }
        }
        catch (TaskCanceledException)
        {
            WriteStatus(fieldName, "TIMEOUT", ConsoleColor.Yellow);
            WriteDetail("Could not reach Qobuz within 10 seconds");
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
}
