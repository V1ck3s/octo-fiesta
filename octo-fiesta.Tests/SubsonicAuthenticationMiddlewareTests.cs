using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Middleware;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicAuthenticationMiddlewareTests
{
    private const string OkResponse = """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""";
    private const string WrongPasswordResponse = """{"subsonic-response":{"status":"failed","version":"1.16.1","error":{"code":40,"message":"Wrong username or password"}}}""";

    private int _pingCount;
    private int _nextCount;
    private Func<HttpResponseMessage> _upstream = () => Ok(OkResponse);

    [Fact]
    public async Task ValidCredentials_AreCachedSoTheNextRequestSkipsThePing()
    {
        var middleware = CreateMiddleware();

        var first = await InvokeAsync(middleware, "u=alice&p=secret");
        var second = await InvokeAsync(middleware, "u=alice&p=secret");

        Assert.Equal(200, first.Response.StatusCode);
        Assert.Equal(200, second.Response.StatusCode);
        Assert.Equal(2, _nextCount);
        Assert.Equal(1, _pingCount);
    }

    [Fact]
    public async Task WrongPassword_Returns401WithCode40()
    {
        _upstream = () => Ok(WrongPasswordResponse);
        var middleware = CreateMiddleware();

        var context = await InvokeAsync(middleware, "u=alice&p=wrong");

        Assert.Equal(401, context.Response.StatusCode);
        Assert.Contains("\"code\": 40", Body(context));
        Assert.Equal(0, _nextCount);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task UnreachableUpstream_Returns503AndSkipsPingsDuringBackoff(Type exceptionType)
    {
        _upstream = () => throw (Exception)Activator.CreateInstance(exceptionType)!;
        var middleware = CreateMiddleware();

        var first = await InvokeAsync(middleware, "u=alice&p=secret");
        var second = await InvokeAsync(middleware, "u=bob&p=other");

        Assert.Equal(503, first.Response.StatusCode);
        Assert.Contains("\"code\": 0", Body(first));
        Assert.Contains("unreachable", Body(first));
        Assert.Equal(503, second.Response.StatusCode);
        Assert.Equal(1, _pingCount);
        Assert.Equal(0, _nextCount);
    }

    [Fact]
    public async Task CachedCredentials_StillPassWhileUpstreamIsDown()
    {
        var middleware = CreateMiddleware();
        await InvokeAsync(middleware, "u=alice&p=secret");

        _upstream = () => throw new HttpRequestException("connection refused");
        await InvokeAsync(middleware, "u=bob&p=other");
        var cached = await InvokeAsync(middleware, "u=alice&p=secret");

        Assert.Equal(200, cached.Response.StatusCode);
        Assert.Equal(2, _nextCount);
    }

    [Fact]
    public async Task UpstreamHttpError_Returns503WithoutBackoff()
    {
        _upstream = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var middleware = CreateMiddleware();

        var first = await InvokeAsync(middleware, "u=alice&p=secret");
        var second = await InvokeAsync(middleware, "u=alice&p=secret");

        Assert.Equal(503, first.Response.StatusCode);
        Assert.Contains("Subsonic server error", Body(first));
        Assert.Equal(503, second.Response.StatusCode);
        Assert.Equal(2, _pingCount);
    }

    [Fact]
    public async Task UnexpectedError_Returns503WithoutBackoff()
    {
        _upstream = () => throw new InvalidOperationException("bug");
        var middleware = CreateMiddleware();

        var first = await InvokeAsync(middleware, "u=alice&p=secret");
        _upstream = () => Ok(OkResponse);
        var second = await InvokeAsync(middleware, "u=alice&p=secret");

        Assert.Equal(503, first.Response.StatusCode);
        Assert.Equal(200, second.Response.StatusCode);
        Assert.Equal(2, _pingCount);
    }

    [Fact]
    public async Task MissingCredentials_Returns401WithoutPing()
    {
        var middleware = CreateMiddleware();

        var context = await InvokeAsync(middleware, "c=client");

        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(0, _pingCount);
    }

    private SubsonicAuthenticationMiddleware CreateMiddleware()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                _pingCount++;
                return _upstream();
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));

        return new SubsonicAuthenticationMiddleware(
            _ =>
            {
                _nextCount++;
                return Task.CompletedTask;
            },
            NullLogger<SubsonicAuthenticationMiddleware>.Instance,
            Options.Create(new SubsonicSettings { Url = "http://navidrome:4533" }),
            factory.Object);
    }

    private static async Task<HttpContext> InvokeAsync(SubsonicAuthenticationMiddleware middleware, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/rest/getAlbumList2";
        context.Request.QueryString = new QueryString($"?{query}&v=1.16.1&f=json");
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, new SubsonicRequestParser());
        return context;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };
}
