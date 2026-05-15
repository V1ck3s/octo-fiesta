using octo_fiesta.Services.SquidWTF;
using octo_fiesta.Models.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;

namespace octo_fiesta.Tests;

public class SquidWTFInstanceManagerTests
{
    private const string InstancesJsonUrl = "https://tidal-uptime.geeked.wtf/";
    private const string FallbackInstance = "https://monochrome-api.samidy.com";

    private static readonly string[] TestInstances = 
    [
        "https://instance1.example.com",
        "https://instance2.example.com",
        "https://instance3.example.com"
    ];

    private static string BuildInstancesJson() =>
        $$"""{"api":["{{string.Join("\",\"", TestInstances)}}"]}""";

    private static string BuildInstancesObjectJson() =>
        """
        {
          "api": [
            { "url": "https://object-instance1.example.com", "version": "2.10" },
            { "url": "https://object-instance2.example.com", "version": "2.9" }
          ]
        }
        """;

    private static SquidWTFInstanceManager CreateManager(
        Mock<HttpMessageHandler> handlerMock,
        string source = "Tidal",
        int timeoutSeconds = 30,
        List<string>? instances = null,
        string? instancesUrl = null)
    {
        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var settings = Options.Create(new SquidWTFSettings
        {
            Source = source,
            InstanceTimeoutSeconds = timeoutSeconds,
            Instances = instances,
            InstancesUrl = instancesUrl
        });

        var loggerMock = new Mock<ILogger<SquidWTFInstanceManager>>();
        return new SquidWTFInstanceManager(factoryMock.Object, settings, loggerMock.Object);
    }

    /// <summary>
    /// Sets up the mock to return the instances JSON on the first call (initialization),
    /// then uses the provided setup for subsequent calls (actual requests).
    /// </summary>
    private static Mock<HttpMessageHandler> CreateHandlerWithInstancesLoaded(
        Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var callIndex = 0;

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                // First call is always the instances.json fetch
                if (request.RequestUri?.ToString() == InstancesJsonUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(BuildInstancesJson())
                    };
                }

                return responseFactory(request, Interlocked.Increment(ref callIndex));
            });

        return handlerMock;
    }

    [Fact]
    public async Task SendWithFailoverAsync_Returns200_DoesNotFailover()
    {
        var handlerMock = CreateHandlerWithInstancesLoaded((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK));

        var manager = CreateManager(handlerMock);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestInstances[0], manager.GetCurrentInstance());
    }

    [Fact]
    public async Task SendWithFailoverAsync_Returns404_DoesNotFailover()
    {
        var handlerMock = CreateHandlerWithInstancesLoaded((_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var manager = CreateManager(handlerMock);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/test"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Should stay on first instance - 404 is a legitimate error, not an instance problem
        Assert.Equal(TestInstances[0], manager.GetCurrentInstance());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SendWithFailoverAsync_ErrorStatusCode_FailsOverToNextInstance(HttpStatusCode statusCode)
    {
        var callCount = 0;
        var handlerMock = CreateHandlerWithInstancesLoaded((_, _) =>
        {
            callCount++;
            // First call returns error, second returns OK
            return callCount == 1
                ? new HttpResponseMessage(statusCode)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var manager = CreateManager(handlerMock);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/track/?id=123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Should have switched to second instance
        Assert.Equal(TestInstances[1], manager.GetCurrentInstance());
    }

    [Fact]
    public async Task SendWithFailoverAsync_AllInstancesFail_ThrowsInvalidOperationException()
    {
        var handlerMock = CreateHandlerWithInstancesLoaded((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        var manager = CreateManager(handlerMock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.SendWithFailoverAsync(
                baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/track/?id=123")));
    }

    [Fact]
    public async Task LoadInstances_WithConfiguredInstances_SkipsRemoteFetch()
    {
        var fetchedRemote = false;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                if (request.RequestUri?.ToString() == InstancesJsonUrl)
                {
                    fetchedRemote = true;
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var manager = CreateManager(
            handlerMock,
            instances: ["https://my-hifi-api.example.com/", "https://backup.example.com"]);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(fetchedRemote, "Remote instances.json must not be fetched when Instances is configured");
        Assert.Equal("https://my-hifi-api.example.com", manager.GetCurrentInstance());
    }

    [Fact]
    public async Task LoadInstances_WithConfiguredInstances_FailsOverWithinList()
    {
        var callCount = 0;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage _, CancellationToken _) =>
            {
                callCount++;
                return callCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });

        var manager = CreateManager(
            handlerMock,
            instances: ["https://primary.example.com", "https://secondary.example.com"]);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://secondary.example.com", manager.GetCurrentInstance());
    }

    [Fact]
    public async Task LoadInstances_WithCustomInstancesUrl_FetchesFromOverride()
    {
        const string customUrl = "https://my-mirror.example.com/instances.json";
        var fetchedUrls = new List<string>();
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                fetchedUrls.Add(url);

                if (url == customUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(BuildInstancesJson())
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var manager = CreateManager(handlerMock, instancesUrl: customUrl);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(customUrl, fetchedUrls);
        Assert.DoesNotContain(InstancesJsonUrl, fetchedUrls);
        Assert.Equal(TestInstances[0], manager.GetCurrentInstance());
    }

    [Fact]
    public async Task SendWithFailoverAsync_FirstTwoFail_ThirdSucceeds()
    {
        var callCount = 0;
        var handlerMock = CreateHandlerWithInstancesLoaded((_, _) =>
        {
            callCount++;
            return callCount <= 2
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var manager = CreateManager(handlerMock);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/track/?id=123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestInstances[2], manager.GetCurrentInstance());
    }

    [Fact]
    public async Task LoadInstances_WithObjectApiFormat_ParsesUrls()
    {
        const string customUrl = "https://my-mirror.example.com/uptime.json";
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var url = request.RequestUri?.ToString();

                if (url == customUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(BuildInstancesObjectJson())
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var manager = CreateManager(handlerMock, instancesUrl: customUrl);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://object-instance1.example.com", manager.GetCurrentInstance());
    }

    [Fact]
    public async Task LoadInstances_WithInvalidApiFormat_UsesFallbackInstance()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var url = request.RequestUri?.ToString();

                if (url == InstancesJsonUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"api\":[{\"version\":\"2.10\"}]}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var manager = CreateManager(handlerMock);

        var response = await manager.SendWithFailoverAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FallbackInstance, manager.GetCurrentInstance());
    }
}
