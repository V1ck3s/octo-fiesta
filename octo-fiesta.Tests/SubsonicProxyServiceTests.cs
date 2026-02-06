using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Subsonic;
using System.Net;

namespace octo_fiesta.Tests;

public class SubsonicProxyServiceTests
{
    private readonly SubsonicProxyService _service;
    private readonly Mock<HttpMessageHandler> _mockHttpMessageHandler;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;

    public SubsonicProxyServiceTests()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_mockHttpMessageHandler.Object);

        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var settings = Options.Create(new SubsonicSettings 
        { 
            Url = "http://localhost:4533" 
        });

        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = httpContext
        };

        _service = new SubsonicProxyService(_mockHttpClientFactory.Object, settings, httpContextAccessor);
    }

    [Fact]
    public async Task RelayAsync_SuccessfulRequest_ReturnsBodyAndContentType()
    {
        // Arrange
        var responseContent = new byte[] { 1, 2, 3, 4, 5 };
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseContent)
        };
        responseMessage.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string>
        {
            { "u", "admin" },
            { "p", "password" },
            { "v", "1.16.0" }
        };

        // Act
        var (body, contentType) = await _service.RelayAsync("rest/ping", parameters);

        // Assert
        Assert.Equal(responseContent, body);
        Assert.Equal("application/json", contentType);
    }

    [Fact]
    public async Task RelayAsync_BuildsCorrectUrl()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => capturedRequest = req)
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string>
        {
            { "u", "admin" },
            { "p", "secret" }
        };

        // Act
        await _service.RelayAsync("rest/ping", parameters);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Contains("http://localhost:4533/rest/ping", capturedRequest!.RequestUri!.ToString());
        Assert.Contains("u=admin", capturedRequest.RequestUri.ToString());
        Assert.Contains("p=secret", capturedRequest.RequestUri.ToString());
    }

    [Fact]
    public async Task RelayAsync_EncodesSpecialCharacters()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => capturedRequest = req)
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string>
        {
            { "query", "rock & roll" },
            { "artist", "AC/DC" }
        };

        // Act
        await _service.RelayAsync("rest/search3", parameters);

        // Assert
        Assert.NotNull(capturedRequest);
        var url = capturedRequest!.RequestUri!.ToString();
        // HttpClient automatically applies URL encoding when building the URI
        // Space can be encoded as + or %20, & as %26, / as %2F
        Assert.Contains("query=", url);
        Assert.Contains("artist=", url);
        Assert.Contains("AC%2FDC", url); // / should be encoded as %2F
    }

    [Fact]
    public async Task RelayAsync_HttpError_ThrowsException()
    {
        // Arrange
        var responseMessage = new HttpResponseMessage(HttpStatusCode.NotFound);

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string> { { "u", "admin" } };

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => 
            _service.RelayAsync("rest/ping", parameters));
    }

    [Fact]
    public async Task RelaySafeAsync_SuccessfulRequest_ReturnsSuccessTrue()
    {
        // Arrange
        var responseContent = new byte[] { 1, 2, 3 };
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseContent)
        };
        responseMessage.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string> { { "u", "admin" } };

        // Act
        var (body, contentType, success) = await _service.RelaySafeAsync("rest/ping", parameters);

        // Assert
        Assert.True(success);
        Assert.Equal(responseContent, body);
        Assert.StartsWith("application/xml", contentType);
    }

    [Fact]
    public async Task RelaySafeAsync_HttpError_ReturnsSuccessFalse()
    {
        // Arrange
        var responseMessage = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string> { { "u", "admin" } };

        // Act
        var (body, contentType, success) = await _service.RelaySafeAsync("rest/ping", parameters);

        // Assert
        Assert.False(success);
        Assert.Null(body);
        Assert.Null(contentType);
    }

    [Fact]
    public async Task RelaySafeAsync_NetworkException_ReturnsSuccessFalse()
    {
        // Arrange
        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var parameters = new Dictionary<string, string> { { "u", "admin" } };

        // Act
        var (body, contentType, success) = await _service.RelaySafeAsync("rest/ping", parameters);

        // Assert
        Assert.False(success);
        Assert.Null(body);
        Assert.Null(contentType);
    }

    [Fact]
    public async Task RelayStreamAsync_SuccessfulRequest_ReturnsFileStreamResult()
    {
        // Arrange
        var streamContent = new byte[] { 1, 2, 3, 4, 5 };
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(streamContent)
        };
        responseMessage.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string> 
        { 
            { "id", "song123" },
            { "u", "admin" }
        };

        // Act
        var result = await _service.RelayStreamAsync(parameters, CancellationToken.None);

        // Assert
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("audio/mpeg", fileResult.ContentType);
        Assert.True(fileResult.EnableRangeProcessing);
    }

    [Fact]
    public async Task RelayStreamAsync_HttpError_ReturnsStatusCodeResult()
    {
        // Arrange
        var responseMessage = new HttpResponseMessage(HttpStatusCode.NotFound);

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string> { { "id", "song123" } };

        // Act
        var result = await _service.RelayStreamAsync(parameters, CancellationToken.None);

        // Assert
        var statusResult = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(404, statusResult.StatusCode);
    }

    [Fact]
    public async Task RelayStreamAsync_Exception_ReturnsObjectResultWith500()
    {
        // Arrange
        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        var parameters = new Dictionary<string, string> { { "id", "song123" } };

        // Act
        var result = await _service.RelayStreamAsync(parameters, CancellationToken.None);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task RelayStreamAsync_DefaultContentType_UsesAudioMpeg()
    {
        // Arrange
        var streamContent = new byte[] { 1, 2, 3 };
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(streamContent)
            // No ContentType set
        };

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        var parameters = new Dictionary<string, string> { { "id", "song123" } };

        // Act
        var result = await _service.RelayStreamAsync(parameters, CancellationToken.None);

        // Assert
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("audio/mpeg", fileResult.ContentType);
    }

    [Fact]
    public async Task RelayStreamAsync_WithRangeHeader_ForwardsRangeToUpstream()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var streamContent = new byte[] { 1, 2, 3, 4, 5 };
        var responseMessage = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(streamContent)
        };
        responseMessage.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => capturedRequest = req)
            .ReturnsAsync(responseMessage);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Range"] = "bytes=0-1023";
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var service = new SubsonicProxyService(_mockHttpClientFactory.Object, 
            Options.Create(new SubsonicSettings { Url = "http://localhost:4533" }), 
            httpContextAccessor);

        var parameters = new Dictionary<string, string> { { "id", "song123" } };

        // Act
        await service.RelayStreamAsync(parameters, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("Range"));
        Assert.Equal("bytes=0-1023", capturedRequest.Headers.GetValues("Range").First());
    }

    [Fact]
    public async Task RelayStreamAsync_WithIfRangeHeader_ForwardsIfRangeToUpstream()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var streamContent = new byte[] { 1, 2, 3 };
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(streamContent)
        };

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => capturedRequest = req)
            .ReturnsAsync(responseMessage);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["If-Range"] = "\"etag123\"";
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var service = new SubsonicProxyService(_mockHttpClientFactory.Object,
            Options.Create(new SubsonicSettings { Url = "http://localhost:4533" }),
            httpContextAccessor);

        var parameters = new Dictionary<string, string> { { "id", "song123" } };

        // Act
        await service.RelayStreamAsync(parameters, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("If-Range"));
    }

    [Fact]
    public async Task RelayStreamAsync_NullHttpContext_ReturnsError()
    {
        // Arrange
        var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
        var service = new SubsonicProxyService(_mockHttpClientFactory.Object,
            Options.Create(new SubsonicSettings { Url = "http://localhost:4533" }),
            httpContextAccessor);

        var parameters = new Dictionary<string, string> { { "id", "song123" } };

        // Act
        var result = await service.RelayStreamAsync(parameters, CancellationToken.None);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
    }
}
