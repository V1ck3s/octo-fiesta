using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Tidal;

namespace octo_fiesta.Services.Tidal;

/// <summary>
/// Owns the Tidal OAuth 2.0 credentials: the login, PKCE or device authorization depending
/// on the client, the token refresh and the country code every catalogue call needs. Tokens come from the token store, or from
/// configuration when they are injected as secrets, and every renewal is written back.
/// </summary>
public class TidalAuthService
{
    /// <summary>
    /// Scope of the device authorization flow. PKCE clients are granted a narrower one,
    /// see <see cref="TidalPkce.Scope"/>.
    /// </summary>
    private const string DeviceScope = "r_usr+w_usr+w_sub";

    /// <summary>
    /// Built-in client. Used when the configuration leaves the client blank, which is
    /// the normal case: it identifies the application, not the user, so it ships working.
    /// This one is public, so it carries no secret and logs in with PKCE. It is also the
    /// only client entitled to the LOSSLESS tier, the limited input ones cap at HIGH and
    /// serve AAC where FLAC was asked for.
    /// </summary>
    private const string DefaultClientId = "49YxDN9a2aFV6RTG";

    public const string AuthBaseUrl = "https://auth.tidal.com/v1/oauth2";

    /// <summary>
    /// Sub-status returned with HTTP 400 while the user has not approved the device yet.
    /// </summary>
    private const int AuthorizationPendingSubStatus = 1002;

    /// <summary>
    /// Renew this long before the access token actually expires, so a download never
    /// starts with a token that dies mid-transfer.
    /// </summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly TidalTokenStore _tokenStore;
    private readonly ILogger<TidalAuthService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly TidalTokens _tokens;
    private readonly string _clientId;

    /// <summary>
    /// Null for a public client, which is the default. Only a limited input device client
    /// has a secret, and having one is what makes the device flow usable.
    /// </summary>
    private readonly string? _clientSecret;

    public TidalAuthService(
        IHttpClientFactory httpClientFactory,
        IOptions<TidalSettings> settings,
        TidalTokenStore tokenStore,
        ILogger<TidalAuthService> logger)
    {
        _httpClient = httpClientFactory.CreateClient(TidalHttpClientConfiguration.AuthClientName);
        _tokenStore = tokenStore;
        _logger = logger;

        var tidalSettings = settings.Value;
        _clientId = Coalesce(tidalSettings.ClientId, DefaultClientId)!;
        _clientSecret = Coalesce(tidalSettings.ClientSecret, null);

        var stored = tokenStore.Load();

        // Tokens are bound to the client that issued them. After a client change the stored
        // pair can only produce a confusing rejection, so drop it and ask for a new login.
        if (stored?.ClientId is { Length: > 0 } storedClient && storedClient != _clientId)
        {
            logger.LogWarning(
                "The Tidal token store was written for client {StoredClient} but {ClientId} is configured. "
                + "Ignoring the stored tokens, run the login helper with --tidal-login",
                storedClient, _clientId);
            stored = null;
        }

        // Explicit configuration wins over the store, so tokens can be injected as secrets.
        var fromConfiguration = !string.IsNullOrWhiteSpace(tidalSettings.RefreshToken)
                                || !string.IsNullOrWhiteSpace(tidalSettings.AccessToken);

        _tokens = new TidalTokens
        {
            AccessToken = Coalesce(tidalSettings.AccessToken, stored?.AccessToken),
            RefreshToken = Coalesce(tidalSettings.RefreshToken, stored?.RefreshToken),
            UserId = Coalesce(tidalSettings.UserId, stored?.UserId),
            CountryCode = Coalesce(tidalSettings.CountryCode, stored?.CountryCode),
            // A token supplied by configuration carries no expiry, so treat it as already
            // due for renewal and let the refresh token mint a fresh one on first use.
            ExpiresAt = fromConfiguration ? null : stored?.ExpiresAt,
            ClientId = _clientId
        };
    }

    /// <summary>
    /// True when the provider has something to authenticate with.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_tokens.RefreshToken) || !string.IsNullOrWhiteSpace(_tokens.AccessToken);

    /// <summary>
    /// Which login the configured client accepts. A secret means a limited input device
    /// client and the device flow, no secret means a public client and PKCE.
    /// </summary>
    public bool UsesDeviceAuthorization => _clientSecret is not null;

    public string? UserId => _tokens.UserId;

    /// <summary>
    /// Country code driving catalogue availability. Falls back to US until the session
    /// resolves the account's own country.
    /// </summary>
    public string CountryCode => string.IsNullOrWhiteSpace(_tokens.CountryCode) ? "US" : _tokens.CountryCode;

    public string TokenStorePath => _tokenStore.Path;

    /// <summary>
    /// Country code to send with catalogue calls. Renews the token first, because a cold
    /// start only learns the account's country from the token response.
    /// </summary>
    public async Task<string> GetCountryCodeAsync(CancellationToken cancellationToken = default)
    {
        await GetAccessTokenAsync(cancellationToken);
        return CountryCode;
    }

    /// <summary>
    /// Returns a usable access token, renewing it when it is missing or close to expiry.
    /// </summary>
    /// <exception cref="InvalidOperationException">No credentials, or the refresh token was revoked.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Tidal is not authenticated. Run the login helper with --tidal-login.");
        }

        if (!NeedsRenewal())
        {
            return _tokens.AccessToken!;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have renewed while we waited for the lock.
            if (!NeedsRenewal())
            {
                return _tokens.AccessToken!;
            }

            await RefreshAsync(cancellationToken);
            return _tokens.AccessToken!;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Builds an authenticated request against api.tidal.com, renewing the token if needed.
    /// </summary>
    public async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method, string url, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Checks the current token against /v1/sessions and caches the user id and country
    /// code it reports. Returns null when the token is not accepted.
    /// </summary>
    public async Task<TidalSession?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        var request = await CreateAuthenticatedRequestAsync(
            HttpMethod.Get, $"{TidalHttpClientConfiguration.ApiBaseUrl}/sessions", cancellationToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Tidal session check returned {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var session = JsonSerializer.Deserialize<TidalSession>(json);
        if (session is null)
        {
            return null;
        }

        var userId = session.UserId.ToString();
        if (_tokens.UserId != userId || _tokens.CountryCode != session.CountryCode)
        {
            _tokens.UserId = userId;
            _tokens.CountryCode = session.CountryCode;
            await PersistAsync(cancellationToken);
        }

        return session;
    }

    /// <summary>
    /// Reads the account's subscription, which decides the highest streamable quality.
    /// </summary>
    public async Task<TidalSubscriptionResponse?> GetSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_tokens.UserId))
        {
            await GetSessionAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(_tokens.UserId))
        {
            return null;
        }

        var url = $"{TidalHttpClientConfiguration.ApiBaseUrl}/users/{_tokens.UserId}/subscription?countryCode={CountryCode}";
        var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, url, cancellationToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Tidal subscription lookup returned {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TidalSubscriptionResponse>(json);
    }

    #region PKCE authorization

    /// <summary>
    /// Address the user opens in a browser to log in. Tidal's login page sits behind a bot
    /// check, so it can only be driven by a real browser, never from here.
    /// </summary>
    public string BuildAuthorizationUrl(TidalPkceChallenge challenge)
        => TidalPkce.BuildAuthorizationUrl(_clientId, challenge);

    /// <summary>
    /// Exchanges the code carried by the redirect for tokens and stores them.
    /// </summary>
    /// <exception cref="InvalidOperationException">No code in the address, or Tidal refused it.</exception>
    public async Task<TidalTokens> CompleteAuthorizationAsync(
        string? redirectedUrl, TidalPkceChallenge challenge, CancellationToken cancellationToken = default)
    {
        var code = TidalPkce.ExtractAuthorizationCode(redirectedUrl)
                   ?? throw new InvalidOperationException(
                       $"No authorization code in what was pasted. It has to be the "
                       + $"{TidalPkce.RedirectUri}?code=... address found in the browser network log, "
                       + "or the value of its code parameter.");

        var form = NewTokenForm();
        form["grant_type"] = "authorization_code";
        form["code"] = code;
        form["redirect_uri"] = TidalPkce.RedirectUri;
        form["code_verifier"] = challenge.CodeVerifier;
        form["client_unique_key"] = challenge.ClientUniqueKey;

        var (tokenResponse, error) = await RequestTokenAsync(form, cancellationToken);
        if (tokenResponse is null)
        {
            throw new InvalidOperationException(
                $"Tidal refused the authorization code: {error?.Error ?? "unknown error"} "
                + $"({error?.ErrorDescription ?? "no description"}). "
                + "A code is single use and expires quickly, so run the helper again for a fresh one.");
        }

        await ApplyTokenResponseAsync(tokenResponse, cancellationToken);
        return _tokens;
    }

    #endregion

    #region Device authorization

    /// <summary>
    /// Starts the device authorization flow and returns the code the user has to approve.
    /// </summary>
    public async Task<TidalDeviceAuthorizationResponse> StartDeviceAuthorizationAsync(
        CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = DeviceScope
        });

        using var response = await _httpClient.PostAsync($"{AuthBaseUrl}/device_authorization", content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Tidal refused the device authorization request ({(int)response.StatusCode}): {json}");
        }

        return JsonSerializer.Deserialize<TidalDeviceAuthorizationResponse>(json)
               ?? throw new InvalidOperationException("Tidal returned an empty device authorization response.");
    }

    /// <summary>
    /// Polls until the user approves the device, then stores the resulting tokens.
    /// </summary>
    /// <exception cref="TimeoutException">The device code expired before approval.</exception>
    public async Task<TidalTokens> WaitForDeviceApprovalAsync(
        TidalDeviceAuthorizationResponse authorization, CancellationToken cancellationToken = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(authorization.Interval, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresIn > 0 ? authorization.ExpiresIn : 300);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken);

            var form = NewTokenForm();
            form["device_code"] = authorization.DeviceCode ?? "";
            form["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code";

            var (tokenResponse, error) = await RequestTokenAsync(form, cancellationToken);

            if (tokenResponse is not null)
            {
                await ApplyTokenResponseAsync(tokenResponse, cancellationToken);
                return _tokens;
            }

            // Anything other than "still waiting" means the flow failed and retrying is pointless.
            if (error?.SubStatus != AuthorizationPendingSubStatus)
            {
                throw new InvalidOperationException(
                    $"Tidal device authorization failed: {error?.Error ?? "unknown error"} "
                    + $"({error?.ErrorDescription ?? "no description"})");
            }
        }

        throw new TimeoutException("The Tidal device code expired before it was approved.");
    }

    #endregion

    #region Token renewal

    private bool NeedsRenewal()
    {
        if (string.IsNullOrWhiteSpace(_tokens.AccessToken))
        {
            return true;
        }

        return _tokens.ExpiresAt is null || _tokens.ExpiresAt.Value - RenewBefore <= DateTimeOffset.UtcNow;
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_tokens.RefreshToken))
        {
            // Nothing to refresh with. An access token supplied on its own is used as-is
            // until Tidal rejects it.
            if (!string.IsNullOrWhiteSpace(_tokens.AccessToken))
            {
                return;
            }

            throw new InvalidOperationException(
                "No Tidal refresh token available. Run the login helper with --tidal-login.");
        }

        var form = NewTokenForm();
        form["refresh_token"] = _tokens.RefreshToken!;
        form["grant_type"] = "refresh_token";

        var (tokenResponse, error) = await RequestTokenAsync(form, cancellationToken);

        if (tokenResponse is null)
        {
            throw new InvalidOperationException(
                $"Tidal refused to renew the access token: {error?.Error ?? "unknown error"} "
                + $"({error?.ErrorDescription ?? "no description"}). "
                + "The refresh token was probably revoked, run the login helper with --tidal-login.");
        }

        _logger.LogInformation("Renewed the Tidal access token, valid for {Seconds}s", tokenResponse.ExpiresIn);
        await ApplyTokenResponseAsync(tokenResponse, cancellationToken);
    }

    /// <summary>
    /// Fields every grant carries. A public client has no secret to send, and the scope
    /// follows the client rather than the grant.
    /// </summary>
    private Dictionary<string, string> NewTokenForm()
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = UsesDeviceAuthorization ? DeviceScope : TidalPkce.Scope
        };

        if (_clientSecret is not null)
        {
            form["client_secret"] = _clientSecret;
        }

        return form;
    }

    /// <summary>
    /// Posts to the token endpoint. The client secret goes in the body rather than in an
    /// HTTP Basic header: Tidal's CDN rejects Basic-authenticated token calls outright.
    /// </summary>
    private async Task<(TidalTokenResponse? Token, TidalAuthError? Error)> RequestTokenAsync(
        Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync($"{AuthBaseUrl}/token", content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (JsonSerializer.Deserialize<TidalTokenResponse>(json), null);
        }

        TidalAuthError? error = null;
        try
        {
            error = JsonSerializer.Deserialize<TidalAuthError>(json);
        }
        catch (JsonException)
        {
            // Non-JSON bodies come from the CDN rather than from Tidal itself.
            _logger.LogWarning("Tidal token endpoint returned {StatusCode} with a non-JSON body", response.StatusCode);
        }

        return (null, error ?? new TidalAuthError
        {
            Error = response.StatusCode == HttpStatusCode.Forbidden ? "forbidden" : "http_error",
            ErrorDescription = $"HTTP {(int)response.StatusCode}",
            Status = (int)response.StatusCode
        });
    }

    private async Task ApplyTokenResponseAsync(TidalTokenResponse tokenResponse, CancellationToken cancellationToken)
    {
        _tokens.AccessToken = tokenResponse.AccessToken;
        _tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

        // A refresh grant returns no new refresh token, so keep the one we already have.
        if (!string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
        {
            _tokens.RefreshToken = tokenResponse.RefreshToken;
        }

        if (tokenResponse.User is not null)
        {
            _tokens.UserId = tokenResponse.User.UserId.ToString();
            _tokens.CountryCode = Coalesce(tokenResponse.User.CountryCode, _tokens.CountryCode);
        }

        await PersistAsync(cancellationToken);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _tokenStore.SaveAsync(_tokens, cancellationToken);
        }
        catch (Exception ex)
        {
            // Losing the write only costs a re-login later, so it must not break playback now.
            _logger.LogError(ex,
                "Could not write the Tidal token store at {Path}. Renewals will not survive a restart",
                _tokenStore.Path);
        }
    }

    #endregion

    private static string? Coalesce(string? preferred, string? fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
