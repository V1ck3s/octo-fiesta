namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the Tidal downloader and metadata service.
/// Tidal uses an OAuth 2.0 device authorization flow, so tokens normally come from the
/// token store written by <c>--tidal-login</c> rather than from configuration.
/// </summary>
public class TidalSettings
{
    /// <summary>
    /// Path to the JSON file holding the OAuth tokens.
    /// Must stay writable: every token renewal is written back to it.
    /// </summary>
    public string TokenStore { get; set; } = "./tidal-tokens.json";

    /// <summary>
    /// Access token. Short-lived (about four hours) and renewed automatically.
    /// Only needed to inject tokens obtained elsewhere; takes precedence over the token store.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Refresh token used to mint new access tokens.
    /// Only needed to inject tokens obtained elsewhere; takes precedence over the token store.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Tidal user ID. Resolved from the session when left empty.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Two-letter country code driving catalogue availability.
    /// Resolved from the account when left empty.
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>
    /// Client id of the Tidal device client the provider authenticates as. This identifies
    /// the application, not your account, so every install shares it and logging in does not
    /// change it. Tidal retires device clients over time, and a retired one still signs in
    /// and reads the catalogue while refusing playback and token renewal. Override this pair
    /// to switch to a working client without waiting for a new release.
    /// Leave empty to use the built-in default.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Client secret matching <see cref="ClientId"/>. Leave empty to use the built-in default.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Preferred audio quality: HI_RES_LOSSLESS, LOSSLESS, HIGH, LOW.
    /// If not specified or unavailable, the highest quality available for the
    /// subscription is used.
    /// </summary>
    public string? Quality { get; set; }
}
