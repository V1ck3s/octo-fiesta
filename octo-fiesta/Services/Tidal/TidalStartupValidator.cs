using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.Tidal;

/// <summary>
/// Validates the Tidal credentials at startup and reports the account the provider will use.
/// </summary>
public class TidalStartupValidator : BaseStartupValidator
{
    private readonly TidalSettings _settings;
    private readonly TidalAuthService? _auth;

    public override string ServiceName => "Tidal";

    public TidalStartupValidator(
        IOptions<TidalSettings> settings,
        HttpClient httpClient,
        IServiceProvider serviceProvider)
        : base(httpClient)
    {
        _settings = settings.Value;
        // Only registered when Tidal is the configured music service.
        _auth = serviceProvider.GetService<TidalAuthService>();
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        if (_auth is null || !_auth.IsConfigured)
        {
            WriteStatus("Tidal credentials", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Run the login helper: dotnet run -- --tidal-login (or docker compose run --rm octo-fiesta --tidal-login)");
            WriteDetail($"Token store: {Path.GetFullPath(_settings.TokenStore)}");
            return ValidationResult.NotConfigured("Tidal credentials not configured");
        }

        if (!TidalQuality.IsValid(_settings.Quality))
        {
            WriteStatus("Tidal Quality", "INVALID", ConsoleColor.Red);
            WriteDetail($"Quality '{_settings.Quality}' is not valid. Set Tidal__Quality to one of:");
            WriteDetail(string.Join(", ", TidalQuality.ValidQualities));
            return ValidationResult.NotConfigured("Invalid Tidal Quality setting");
        }

        WriteStatus("Tidal Quality", TidalQuality.Normalize(_settings.Quality), ConsoleColor.Cyan);
        WriteStatus("Tidal Token store", Path.GetFullPath(_auth.TokenStorePath), ConsoleColor.Cyan);

        try
        {
            var accessToken = await _auth.GetAccessTokenAsync(cancellationToken);
            WriteStatus("Tidal AccessToken", MaskSecret(accessToken), ConsoleColor.Cyan);

            var session = await _auth.GetSessionAsync(cancellationToken);
            if (session is null)
            {
                WriteStatus("Tidal credentials", "INVALID", ConsoleColor.Red);
                WriteDetail("Tidal rejected the access token. Run the login helper again with --tidal-login");
                return ValidationResult.Failure("INVALID", "Tidal rejected the access token");
            }

            var subscription = await _auth.GetSubscriptionAsync(cancellationToken);

            WriteStatus("Tidal credentials", "VALID", ConsoleColor.Green);
            WriteDetail($"User ID: {session.UserId}");
            WriteDetail($"Country: {session.CountryCode}");
            WriteDetail($"Subscription: {subscription?.Subscription?.Type ?? "unknown"}");

            if (subscription?.HighestSoundQuality is { Length: > 0 } highest)
            {
                WriteDetail($"Highest available quality: {highest}");
            }

            // Credentials can be perfectly valid on an account that is not allowed to
            // stream anything. Say so here rather than let every download fail later.
            if (subscription is { PremiumAccess: false })
            {
                WriteStatus("Tidal playback", "NOT ENTITLED", ConsoleColor.Yellow);
                WriteDetail("This account has no active subscription, so no track can be downloaded");
                WriteDetail("Search and browsing still work. Subscribe to at least HiFi to stream full tracks");
                return ValidationResult.Failure("NO SUBSCRIPTION",
                    "Tidal account has no playback entitlement", ConsoleColor.Yellow);
            }

            return ValidationResult.Success("Tidal validation completed");
        }
        catch (Exception ex)
        {
            var result = HandleException(ex, "Tidal credentials");
            WriteValidationResult("Tidal credentials", result);
            WriteDetail("Run the login helper again with --tidal-login if the refresh token was revoked");
            return result;
        }
    }
}
