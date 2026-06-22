using System.Net;
using System.Text.Json;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Keeps the Amazon SquidWTF browser session (cookies + webNonce) on one HttpClient
/// so captcha tokens and API calls stay tied to the same page session.
/// </summary>
internal sealed class SquidWTFAmazonSession : IDisposable
{
    public HttpClient Http { get; }
    public CookieContainer Cookies { get; }
    public string WebNonce { get; set; } = "";

    public string? CaptchaToken { get; set; }
    public DateTimeOffset CaptchaTokenExpiresAt { get; set; }

    public SquidWTFAmazonSession()
    {
        Cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        Http = new HttpClient(handler);
    }

    public void Dispose() => Http.Dispose();
}
