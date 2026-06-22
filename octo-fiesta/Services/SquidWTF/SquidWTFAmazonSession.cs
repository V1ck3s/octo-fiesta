using Primp;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Keeps the Amazon SquidWTF browser session (cookies + webNonce) on one Primp client
/// with Chrome TLS impersonation so /api/search passes edgedragon checks.
/// </summary>
internal sealed class SquidWTFAmazonSession : IDisposable
{
    public PrimpClient Client { get; }
    public string WebNonce { get; set; } = "";

    public string? CaptchaToken { get; set; }
    public DateTimeOffset CaptchaTokenExpiresAt { get; set; }

    public SquidWTFAmazonSession()
    {
        Client = PrimpClient.Builder()
            .WithImpersonate(Impersonate.Chrome146)
            .WithOS(ImpersonateOS.Windows)
            .WithTimeout(TimeSpan.FromSeconds(90))
            .WithCookieStore(true)
            .FollowRedirects(true)
            .Build();
    }

    public void Dispose() => Client.Dispose();
}
