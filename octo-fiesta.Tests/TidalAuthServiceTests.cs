using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Tidal;
using octo_fiesta.Services.Tidal;

namespace octo_fiesta.Tests;

/// <summary>
/// Covers the OAuth device flow, the token renewal that keeps the provider authenticated,
/// and the token store that has to survive a restart.
/// </summary>
public class TidalAuthServiceTests : IDisposable
{
    private readonly string _storeDirectory;
    private readonly string _storePath;

    public TidalAuthServiceTests()
    {
        _storeDirectory = Path.Combine(Path.GetTempPath(), "octo-fiesta-tidal-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_storeDirectory);
        _storePath = Path.Combine(_storeDirectory, "tidal-tokens.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDirectory))
        {
            Directory.Delete(_storeDirectory, true);
        }
    }

    #region Token store

    [Fact]
    public async Task TokenStore_RoundTripsTokens()
    {
        var store = TidalTestFactory.TokenStore(_storePath);
        var expiry = DateTimeOffset.UtcNow.AddDays(7);

        await store.SaveAsync(new TidalTokens
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = expiry,
            UserId = "123456789",
            CountryCode = "FR"
        });

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("access", loaded!.AccessToken);
        Assert.Equal("refresh", loaded.RefreshToken);
        Assert.Equal("123456789", loaded.UserId);
        Assert.Equal("FR", loaded.CountryCode);
        Assert.Equal(expiry.ToUnixTimeSeconds(), loaded.ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void TokenStore_MissingFile_ReturnsNull()
    {
        Assert.Null(TidalTestFactory.TokenStore(Path.Combine(_storeDirectory, "absent.json")).Load());
    }

    [Fact]
    public void TokenStore_CorruptedFile_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllText(_storePath, "not json at all");

        Assert.Null(TidalTestFactory.TokenStore(_storePath).Load());
    }

    [Fact]
    public async Task TokenStore_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_storeDirectory, "config", "tidal-tokens.json");
        var store = TidalTestFactory.TokenStore(nested);

        await store.SaveAsync(new TidalTokens { RefreshToken = "refresh" });

        Assert.True(File.Exists(nested));
    }

    #endregion

    #region Configuration

    [Fact]
    public void IsConfigured_WithoutAnyToken_IsFalse()
    {
        var auth = TidalTestFactory.AuthService(new TidalStubHandler(), _storePath, new TidalSettings());

        Assert.False(auth.IsConfigured);
    }

    [Fact]
    public void Credentials_ComeFromTheTokenStoreWhenConfigurationIsEmpty()
    {
        File.WriteAllText(_storePath, JsonSerializer.Serialize(new TidalTokens
        {
            AccessToken = "stored-access",
            RefreshToken = "stored-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            UserId = "42",
            CountryCode = "DE"
        }));

        var auth = TidalTestFactory.AuthService(new TidalStubHandler(), _storePath, new TidalSettings());

        Assert.True(auth.IsConfigured);
        Assert.Equal("42", auth.UserId);
        Assert.Equal("DE", auth.CountryCode);
    }

    [Fact]
    public void StoredTokensFromAnotherClient_AreIgnored()
    {
        // Tokens are bound to the client that issued them, so keeping them after a client
        // change would only produce a "Client id <n> not found" rejection later.
        File.WriteAllText(_storePath, JsonSerializer.Serialize(new TidalTokens
        {
            AccessToken = "stored-access",
            RefreshToken = "stored-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(3),
            ClientId = "aRetiredClient"
        }));

        var auth = TidalTestFactory.AuthService(new TidalStubHandler(), _storePath, new TidalSettings());

        Assert.False(auth.IsConfigured);
    }

    [Fact]
    public void StoredTokensFromTheSameClient_AreKept()
    {
        File.WriteAllText(_storePath, JsonSerializer.Serialize(new TidalTokens
        {
            AccessToken = "stored-access",
            RefreshToken = "stored-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(3),
            ClientId = new TidalSettings().ClientId
        }));

        var auth = TidalTestFactory.AuthService(new TidalStubHandler(), _storePath, new TidalSettings());

        Assert.True(auth.IsConfigured);
    }

    [Fact]
    public async Task BlankClient_FallsBackToTheBuiltInDefault()
    {
        // The normal case: nothing configured. The token calls must still carry a client.
        var handler = new TidalStubHandler().Respond("oauth2/token", TidalTestFactory.TokenResponse);
        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings
        {
            RefreshToken = "refresh",
            ClientId = null,
            ClientSecret = null
        });

        await auth.GetAccessTokenAsync();

        var body = handler.Bodies.Single();
        Assert.Contains("client_id=fX2JxdmntZWK0ixT", body);
        Assert.Contains("client_secret=", body);
    }

    [Fact]
    public async Task ConfiguredClient_IsUsedForTheTokenCalls()
    {
        var handler = new TidalStubHandler().Respond("oauth2/token", TidalTestFactory.TokenResponse);
        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings
        {
            RefreshToken = "refresh",
            ClientId = "myClientId",
            ClientSecret = "myClientSecret"
        });

        await auth.GetAccessTokenAsync();

        var body = handler.Bodies.Single();
        Assert.Contains("client_id=myClientId", body);
        Assert.Contains("client_secret=myClientSecret", body);
    }

    [Fact]
    public void CountryCode_FallsBackToUsUntilTheAccountIsKnown()
    {
        var auth = TidalTestFactory.AuthService(new TidalStubHandler(), _storePath,
            new TidalSettings { RefreshToken = "refresh" });

        Assert.Equal("US", auth.CountryCode);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithoutCredentials_PointsAtTheLoginHelper()
    {
        var auth = TidalTestFactory.AuthService(new TidalStubHandler(), _storePath, new TidalSettings());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetAccessTokenAsync());

        Assert.Contains("--tidal-login", exception.Message);
    }

    #endregion

    #region Renewal

    [Fact]
    public async Task GetAccessTokenAsync_WithAValidStoredToken_DoesNotCallTidal()
    {
        File.WriteAllText(_storePath, JsonSerializer.Serialize(new TidalTokens
        {
            AccessToken = "stored-access",
            RefreshToken = "stored-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(3)
        }));

        var handler = new TidalStubHandler();
        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings());

        Assert.Equal("stored-access", await auth.GetAccessTokenAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithAnExpiredToken_RenewsAndPersists()
    {
        File.WriteAllText(_storePath, JsonSerializer.Serialize(new TidalTokens
        {
            AccessToken = "stale-access",
            RefreshToken = "stored-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        }));

        var handler = new TidalStubHandler().Respond("oauth2/token", TidalTestFactory.TokenResponse);
        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings());

        Assert.Equal("fresh-access-token", await auth.GetAccessTokenAsync());

        var persisted = TidalTestFactory.TokenStore(_storePath).Load();
        Assert.Equal("fresh-access-token", persisted!.AccessToken);
        Assert.Equal("FR", persisted.CountryCode);
        Assert.True(persisted.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6));
    }

    [Fact]
    public async Task GetAccessTokenAsync_RenewsOnlyOnceForConcurrentCallers()
    {
        File.WriteAllText(_storePath, JsonSerializer.Serialize(new TidalTokens
        {
            RefreshToken = "stored-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        }));

        var handler = new TidalStubHandler().Respond("oauth2/token", TidalTestFactory.TokenResponse);
        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings());

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => auth.GetAccessTokenAsync()));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithARevokedRefreshToken_ExplainsHowToRecover()
    {
        var handler = new TidalStubHandler().Respond("oauth2/token",
            """{"error":"invalid_grant","error_description":"Refresh token is invalid","status":401,"sub_status":11101}""",
            HttpStatusCode.Unauthorized);

        var auth = TidalTestFactory.AuthService(handler, _storePath,
            new TidalSettings { RefreshToken = "revoked" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetAccessTokenAsync());

        Assert.Contains("invalid_grant", exception.Message);
        Assert.Contains("--tidal-login", exception.Message);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithAnAccessTokenAndNoRefreshToken_UsesItAsIs()
    {
        var handler = new TidalStubHandler();
        var auth = TidalTestFactory.AuthService(handler, _storePath,
            new TidalSettings { AccessToken = "injected-access" });

        Assert.Equal("injected-access", await auth.GetAccessTokenAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConfiguredTokensTakePrecedenceOverTheStore()
    {
        File.WriteAllText(_storePath, JsonSerializer.Serialize(new TidalTokens
        {
            AccessToken = "stored-access",
            RefreshToken = "stored-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(3)
        }));

        var handler = new TidalStubHandler().Respond("oauth2/token", TidalTestFactory.TokenResponse);
        var auth = TidalTestFactory.AuthService(handler, _storePath,
            new TidalSettings { RefreshToken = "configured-refresh" });

        // A configured token carries no expiry, so it is renewed before the first use.
        Assert.Equal("fresh-access-token", await auth.GetAccessTokenAsync());

        Assert.Contains("configured-refresh", handler.Bodies.Single());
    }

    [Fact]
    public async Task RefreshRequest_SendsTheClientSecretInTheBody()
    {
        // Tidal's CDN rejects token calls authenticated with an HTTP Basic header.
        var handler = new TidalStubHandler().Respond("oauth2/token", TidalTestFactory.TokenResponse);
        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings { RefreshToken = "refresh" });

        await auth.GetAccessTokenAsync();

        Assert.Null(handler.Requests.Single().Headers.Authorization);
        Assert.Contains("client_secret=", handler.Bodies.Single());
        Assert.Contains("grant_type=refresh_token", handler.Bodies.Single());
    }

    #endregion

    #region Device authorization

    [Fact]
    public async Task StartDeviceAuthorizationAsync_ReturnsTheCodeToApprove()
    {
        var handler = new TidalStubHandler().Respond("device_authorization", """
            {
              "deviceCode": "device-code",
              "userCode": "ABC12",
              "verificationUri": "link.tidal.com",
              "verificationUriComplete": "link.tidal.com/ABC12",
              "expiresIn": 300,
              "interval": 2
            }
            """);

        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings());
        var authorization = await auth.StartDeviceAuthorizationAsync();

        Assert.Equal("device-code", authorization.DeviceCode);
        Assert.Equal("link.tidal.com/ABC12", authorization.VerificationUriComplete);
        Assert.Equal(2, authorization.Interval);
    }

    [Fact]
    public async Task WaitForDeviceApprovalAsync_KeepsPollingWhileApprovalIsPending()
    {
        var pendingBody =
            """{"error":"authorization_pending","error_description":"not authorized yet","status":400,"sub_status":1002}""";

        var calls = 0;
        var handler = new TidalStubHandler().Respond("oauth2/token", _ =>
        {
            calls++;
            return calls < 3
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(pendingBody) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TidalTestFactory.TokenResponse) };
        });

        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings());
        var tokens = await auth.WaitForDeviceApprovalAsync(new TidalDeviceAuthorizationResponse
        {
            DeviceCode = "device-code",
            ExpiresIn = 300,
            // Poll without waiting; the real interval is seconds.
            Interval = 0
        });

        Assert.Equal(3, calls);
        Assert.Equal("fresh-access-token", tokens.AccessToken);
        Assert.Equal("123456789", tokens.UserId);
        Assert.NotNull(TidalTestFactory.TokenStore(_storePath).Load());
    }

    [Fact]
    public async Task WaitForDeviceApprovalAsync_OnAnyOtherError_StopsImmediately()
    {
        var handler = new TidalStubHandler().Respond("oauth2/token",
            """{"error":"expired_token","error_description":"Device code expired","status":400,"sub_status":1004}""",
            HttpStatusCode.BadRequest);

        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            auth.WaitForDeviceApprovalAsync(new TidalDeviceAuthorizationResponse
            {
                DeviceCode = "device-code",
                ExpiresIn = 300,
                Interval = 0
            }));

        Assert.Contains("expired_token", exception.Message);
        Assert.Single(handler.Requests);
    }

    #endregion

    #region Session

    [Fact]
    public async Task GetSessionAsync_CachesTheAccountCountryCode()
    {
        var handler = new TidalStubHandler()
            .Respond("oauth2/token", TidalTestFactory.TokenResponse)
            .Respond("/sessions", """{"sessionId":"session","userId":123456789,"countryCode":"BE"}""");

        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings { RefreshToken = "refresh" });

        var session = await auth.GetSessionAsync();

        Assert.Equal("BE", session!.CountryCode);
        Assert.Equal("BE", auth.CountryCode);
        Assert.Equal("BE", TidalTestFactory.TokenStore(_storePath).Load()!.CountryCode);
    }

    [Fact]
    public async Task GetSubscriptionAsync_ReadsTheEntitlementOfTheAccount()
    {
        var handler = new TidalStubHandler()
            .Respond("oauth2/token", TidalTestFactory.TokenResponse)
            .Respond("/subscription", """
                {
                  "status": "ACTIVE",
                  "subscription": { "type": "FREE", "offlineGracePeriod": 0 },
                  "highestSoundQuality": null,
                  "premiumAccess": false
                }
                """)
            .Respond("/sessions", """{"sessionId":"session","userId":123456789,"countryCode":"FR"}""");

        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings { RefreshToken = "refresh" });

        var subscription = await auth.GetSubscriptionAsync();

        Assert.Equal("FREE", subscription!.Subscription!.Type);
        Assert.False(subscription.PremiumAccess);
        Assert.Null(subscription.HighestSoundQuality);
    }

    [Fact]
    public async Task GetSessionAsync_WhenTheTokenIsRejected_ReturnsNull()
    {
        var handler = new TidalStubHandler()
            .Respond("oauth2/token", TidalTestFactory.TokenResponse)
            .Respond("/sessions", """{"status":401,"subStatus":11002}""", HttpStatusCode.Unauthorized);

        var auth = TidalTestFactory.AuthService(handler, _storePath, new TidalSettings { RefreshToken = "refresh" });

        Assert.Null(await auth.GetSessionAsync());
    }

    #endregion
}
