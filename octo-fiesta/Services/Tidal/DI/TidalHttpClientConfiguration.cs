namespace octo_fiesta.Services.Tidal;

public static class TidalHttpClientConfiguration
{
    public const string ApiBaseUrl = "https://api.tidal.com/v1";

    /// <summary>
    /// Client for auth.tidal.com and api.tidal.com calls. The Authorization header is set
    /// per request by <see cref="TidalAuthService"/> because the token is renewed in flight.
    /// </summary>
    public const string AuthClientName = "Tidal";

    /// <summary>
    /// Client for the CDN URLs found in a manifest. Those are pre-signed and must not
    /// carry the account's bearer token.
    /// </summary>
    public const string MediaClientName = "TidalMedia";

    /// <summary>
    /// User agent of the device client whose credentials drive the OAuth flow. Tidal's CDN
    /// rejects requests that do not look like a known client.
    /// </summary>
    private const string UserAgent = "TIDAL/3.0 okhttp/3.14.9";

    public static void ConfigureApiClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public static void ConfigureMediaClient(HttpClient client)
    {
        // Long transfers: a Hi-Res track is assembled from hundreds of DASH segments.
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }
}
