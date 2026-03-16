using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Yandex;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.Yandex;

class YandexStartupValidator : BaseStartupValidator
{
    private readonly YandexSettings _settings;

    public YandexStartupValidator(
        IOptions<YandexSettings> settings,
        IHttpClientFactory httpClientFactory)
        : base(httpClientFactory.CreateClient("Yandex"))
    {
        _settings = settings.Value;
    }

    public override string ServiceName => "Yandex";

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_settings.OAuthToken))
        {
            WriteStatus("Yandex OAuthToken", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Yandex__OAuthToken environment variable");
            WriteDetail("https://oauth.yandex.ru/authorize?response_type=token&client_id=23cabbbdc6cd418abb4b39c32c41195d");
            WriteFailure();
            return ValidationResult.NotConfigured("Yandex OAuthToken not configured");
        }
        WriteStatus("OAuthToken", MaskSecret(_settings.OAuthToken), ConsoleColor.Cyan);

        if (!YandexQuality.IsValid(_settings.Quality))
        {
            WriteStatus("Quality", "INVALID", ConsoleColor.Red);
            WriteDetail($"Quality option {_settings.Quality} is not valid");
            WriteDetail("Set the Yandex__Quality environment variable to one of the valid options:");
            WriteDetail(string.Join(",", YandexQuality.ValidQualities));
            WriteFailure();
            return ValidationResult.NotConfigured("Invalid Quality setting");
        }
        WriteStatus("Quality", _settings.Quality ?? "FLAC", ConsoleColor.Cyan);

        try
        {
            ValidationResult? result = await ValidateUserAccount(cancellationToken);
            if (result is not null) return result;
        }
        catch (System.Exception exception)
        {
            var result = HandleException(exception, "Account");
            WriteValidationResult("Account", result);
            WriteFailure();
            return result;
        }
        WriteStatus("Yandex Service Validation", "SUCCESS", ConsoleColor.Green);
        WriteDetail("Yandex Music validation completed");
        return ValidationResult.Success("Yandex Music validation completed");
    }

    private async Task<ValidationResult?> ValidateUserAccount(CancellationToken cancellationToken)
    {

        string accountStatusUrl = "/account/status";
        var response = await _httpClient.GetAsync(accountStatusUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            WriteStatus("Account", "ERROR", ConsoleColor.Red);
            WriteDetail("Unable to get user account status");
            WriteDetail($"Yandex API returned status code {response.StatusCode}");
            WriteFailure();
            return ValidationResult.Failure(
                "Unable to get user account status",
                $"Yandex API returned status code {response.StatusCode}"
            );
        }
        string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        // Playback and downloads work correctly only with Yandex Plus subscription
        // So we check whether user has one
        YandexResponse<YandexUserAccountStatus>? accountStatusResponse = JsonSerializer.Deserialize<YandexResponse<YandexUserAccountStatus>>(responseContent);
        
        if (accountStatusResponse?.Error is not null)
        {
            WriteStatus("Account", "ERROR", ConsoleColor.Red);
            WriteDetail("Unable to parse Yandex API response for account status");
            WriteFailure();
            return ValidationResult.Failure(
                "ERROR",
                "Unable to parse Yandex API response for account status"
            );
        }

        if (accountStatusResponse?.Result?.PlusStatus?.HasPlus != true)
        {
            WriteStatus("Account", "ERROR", ConsoleColor.Red);
            WriteDetail("User has no Yandex Plus subscription");
            WriteDetail("Subscription is required for the service to work");
            WriteFailure();
            return ValidationResult.Failure(
                "Subscription required",
                "Service doesn't work without subscription"
            );
        }
        WriteStatus("Account", "HAS SUBSCRIPTION", ConsoleColor.Green);

        return null;
    }

    private void WriteFailure()
    {
        WriteStatus("Yandex Service Validation", "FAILED", ConsoleColor.Red);
    }
}