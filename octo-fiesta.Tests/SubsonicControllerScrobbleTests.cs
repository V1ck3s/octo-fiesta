using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;
using System.Net;

namespace octo_fiesta.Tests;

public class SubsonicControllerScrobbleTests
{
    private readonly Mock<IMusicMetadataService> _mockMetadataService;
    private readonly Mock<ILocalLibraryService> _mockLocalLibraryService;
    private readonly Mock<IDownloadService> _mockDownloadService;
    private readonly Mock<ILogger<SubsonicController>> _mockLogger;
    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicModelMapper _modelMapper;
    private readonly IOptions<SubsonicSettings> _settings;

    public SubsonicControllerScrobbleTests()
    {
        _mockMetadataService = new Mock<IMusicMetadataService>();
        _mockLocalLibraryService = new Mock<ILocalLibraryService>();
        _mockDownloadService = new Mock<IDownloadService>();
        _mockLogger = new Mock<ILogger<SubsonicController>>();

        _requestParser = new SubsonicRequestParser();
        _responseBuilder = new SubsonicResponseBuilder();
        _modelMapper = new SubsonicModelMapper(
            _responseBuilder,
            new Mock<ILogger<SubsonicModelMapper>>().Object);

        _settings = Options.Create(new SubsonicSettings
        {
            Url = "http://localhost:4533"
        });
    }

    private SubsonicController CreateController(
        Dictionary<string, string> queryParams,
        HttpResponseMessage proxyResponse,
        Action<HttpRequestMessage>? captureRequest = null)
    {
        // We can't easily mock SubsonicProxyService (concrete class with HttpClient dependency)
        // So we create a real one with a mocked HttpClient
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captureRequest?.Invoke(req))
            .ReturnsAsync(proxyResponse);

        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };

        var proxyService = new SubsonicProxyService(
            mockHttpClientFactory.Object, _settings, httpContextAccessor);

        var appLifetimeMock = new Mock<IHostApplicationLifetime>();
        appLifetimeMock.SetupGet(x => x.ApplicationStopping).Returns(CancellationToken.None);

        var controller = new SubsonicController(
            _settings,
            _mockMetadataService.Object,
            _mockLocalLibraryService.Object,
            _mockDownloadService.Object,
            _requestParser,
            _responseBuilder,
            _modelMapper,
            proxyService,
            appLifetimeMock.Object,
            _mockLogger.Object,
            playlistSyncService: null);

        // Set up HttpContext with query parameters
        var httpContext = new DefaultHttpContext();
        var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        httpContext.Request.QueryString = new QueryString("?" + queryString);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    [Fact]
    public async Task Scrobble_WithResolvableExternalId_UsesLocalId()
    {
        // Arrange
        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-qobuz-song-123"))
            .Returns((true, "qobuz", "song", "123"));
        _mockLocalLibraryService
            .Setup(x => x.GetLocalIdForExternalSongAsync("qobuz", "123"))
            .ReturnsAsync("9981");

        HttpRequestMessage? capturedRequest = null;
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "ext-qobuz-song-123" },
                { "submission", "true" },
                { "f", "json" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.Scrobble();

        // Assert
        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(capturedRequest);
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        Assert.Contains("/rest/scrobble", requestUrl);
        Assert.Contains("id=9981", requestUrl);
        Assert.DoesNotContain("ext-qobuz-song-123", requestUrl);
    }

    [Fact]
    public async Task Scrobble_WithUnresolvableExternalId_ReturnsSuccessAndDoesNotRelay()
    {
        // Arrange
        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-deezer-song-999"))
            .Returns((true, "deezer", "song", "999"));
        _mockLocalLibraryService
            .Setup(x => x.GetLocalIdForExternalSongAsync("deezer", "999"))
            .ReturnsAsync((string?)null);

        HttpRequestMessage? capturedRequest = null;
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "ext-deezer-song-999" },
                { "submission", "true" },
                { "f", "xml" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.Scrobble();

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Contains("status=\"ok\"", contentResult.Content ?? "");
        Assert.Null(capturedRequest);
    }
}
