using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Tidal;

namespace octo_fiesta.Tests;

/// <summary>
/// Routes outgoing requests to canned responses keyed by a substring of the URL, so a test
/// can answer the OAuth token endpoint and the catalogue endpoints at the same time.
/// </summary>
internal sealed class TidalStubHandler : HttpMessageHandler
{
    private readonly List<(string UrlFragment, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>
    /// Request bodies captured while sending. The content itself is disposed by the caller,
    /// so a test cannot read it afterwards.
    /// </summary>
    public List<string> Bodies { get; } = [];

    public TidalStubHandler Respond(string urlFragment, string body, HttpStatusCode status = HttpStatusCode.OK)
        => Respond(urlFragment, _ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    public TidalStubHandler Respond(string urlFragment, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((urlFragment, respond));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "");
        var url = request.RequestUri!.ToString();

        foreach (var (fragment, respond) in _routes)
        {
            if (url.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(respond(request));
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No stub for {url}")
        });
    }
}

internal static class TidalTestFactory
{
    /// <summary>
    /// Token endpoint answer used whenever a test does not care about the renewal itself.
    /// </summary>
    public const string TokenResponse = """
        {
          "access_token": "fresh-access-token",
          "refresh_token": "refresh-token",
          "token_type": "Bearer",
          "expires_in": 604800,
          "user": { "userId": 123456789, "countryCode": "FR" }
        }
        """;

    public static IHttpClientFactory HttpClientFactory(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        return factory.Object;
    }

    public static TidalTokenStore TokenStore(string path, TidalSettings? settings = null)
    {
        settings ??= new TidalSettings();
        settings.TokenStore = path;
        return new TidalTokenStore(Options.Create(settings), Mock.Of<ILogger<TidalTokenStore>>());
    }

    public static TidalAuthService AuthService(
        HttpMessageHandler handler, string tokenStorePath, TidalSettings? settings = null)
    {
        settings ??= new TidalSettings { RefreshToken = "refresh-token", CountryCode = "FR" };
        settings.TokenStore = tokenStorePath;

        return new TidalAuthService(
            HttpClientFactory(handler),
            Options.Create(settings),
            TokenStore(tokenStorePath, new TidalSettings()),
            Mock.Of<ILogger<TidalAuthService>>());
    }
}
