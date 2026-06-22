namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Shared Amazon SquidWTF session: curl-impersonate cookies + webNonce + captcha token cache.
/// </summary>
internal sealed class SquidWTFAmazonSession : IDisposable
{
    public SquidWTFAmazonImpersonateHttp Http { get; } = new();
    public string WebNonce { get; set; } = "";

    public string? CaptchaToken { get; set; }
    public DateTimeOffset CaptchaTokenExpiresAt { get; set; }

    public void Dispose() => Http.Dispose();
}
