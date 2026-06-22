using Primp;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Shared Amazon SquidWTF session with Chrome TLS impersonation (Primp) + webNonce + token cache.
/// </summary>
internal sealed class SquidWTFAmazonSession : IDisposable
{
    public PrimpClient Client { get; } = PrimpClient.Builder()
        .WithImpersonate(Impersonate.Chrome146)
        .WithOS(ImpersonateOS.Windows)
        .WithTimeout(TimeSpan.FromSeconds(90))
        .WithCookieStore(true)
        .FollowRedirects(true)
        .Build();

    public string WebNonce { get; set; } = "";

    public string? CaptchaToken { get; set; }
    public DateTimeOffset CaptchaTokenExpiresAt { get; set; }

    public void Dispose() => Client.Dispose();
}
