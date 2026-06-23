namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Shared Amazon SquidWTF session: curl-impersonate cookies + webNonce + captcha token cache.
/// </summary>
internal sealed class SquidWTFAmazonSession : IDisposable
{
    private static readonly TimeSpan PageSessionValidity = TimeSpan.FromMinutes(10);

    public SquidWTFAmazonImpersonateHttp Http { get; } = new();
    public string WebNonce { get; set; } = "";
    public DateTimeOffset PageSessionLoadedAt { get; set; }

    public string? CaptchaToken { get; set; }
    public DateTimeOffset CaptchaTokenExpiresAt { get; set; }

    public bool IsPageSessionStale =>
        string.IsNullOrEmpty(WebNonce) ||
        PageSessionLoadedAt == DateTimeOffset.MinValue ||
        DateTimeOffset.UtcNow >= PageSessionLoadedAt + PageSessionValidity;

    public void Dispose() => Http.Dispose();
}
