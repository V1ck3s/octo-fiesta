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

public class SubsonicControllerStarUnstarExternalIdTests
{
    private readonly Mock<IMusicMetadataService> _mockMetadataService;
    private readonly Mock<ILocalLibraryService> _mockLocalLibraryService;
    private readonly Mock<IDownloadService> _mockDownloadService;
    private readonly Mock<ILogger<SubsonicController>> _mockLogger;
    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicModelMapper _modelMapper;
    private readonly IOptions<SubsonicSettings> _settings;

    public SubsonicControllerStarUnstarExternalIdTests()
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
        Action<HttpRequestMessage>? captureRequest = null,
        IOptions<SubsonicSettings>? customSettings = null)
    {
        var settingsToUse = customSettings ?? _settings;
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
            mockHttpClientFactory.Object, settingsToUse, httpContextAccessor);

        var appLifetimeMock = new Mock<IHostApplicationLifetime>();
        appLifetimeMock.SetupGet(x => x.ApplicationStopping).Returns(CancellationToken.None);

        var controller = new SubsonicController(
            settingsToUse,
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
    public async Task Star_WithResolvableExternalSongId_UsesLocalId()
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
                { "f", "json" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.Star();

        // Assert
        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(capturedRequest);
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        Assert.Contains("/rest/star", requestUrl);
        Assert.Contains("id=9981", requestUrl);
        Assert.DoesNotContain("ext-qobuz-song-123", requestUrl);
    }

    [Fact]
    public async Task Star_WithUnresolvableExternalSongId_InPermanentMode_StartsDownloadAndReturnsSuccess()
    {
        // Permanent mode (default), song not local yet: starring triggers a download and returns success.
        var downloadCompleted = new ManualResetEventSlim(false);

        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-deezer-song-999"))
            .Returns((true, "deezer", "song", "999"));
        _mockLocalLibraryService
            .Setup(x => x.GetLocalIdForExternalSongAsync("deezer", "999"))
            .ReturnsAsync((string?)null);
        _mockDownloadService
            .Setup(x => x.DownloadSongToPermanentAsync("deezer", "999", It.IsAny<CancellationToken>()))
            .ReturnsAsync("local-id-999")
            .Callback(downloadCompleted.Set);

        HttpRequestMessage? capturedRequest = null;
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "ext-deezer-song-999" },
                { "f", "xml" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.Star();

        // Assert
        Assert.True(downloadCompleted.Wait(TimeSpan.FromSeconds(1)), "Download was not scheduled within timeout.");

        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Contains("status=\"ok\"", contentResult.Content ?? "");
        Assert.Null(capturedRequest); // does not relay to the backend
        _mockDownloadService.Verify(
            x => x.DownloadSongToPermanentAsync("deezer", "999", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unstar_WithResolvableExternalSongId_UsesLocalId()
    {
        // Arrange
        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-qobuz-song-321"))
            .Returns((true, "qobuz", "song", "321"));
        _mockLocalLibraryService
            .Setup(x => x.GetLocalIdForExternalSongAsync("qobuz", "321"))
            .ReturnsAsync("2001");

        HttpRequestMessage? capturedRequest = null;
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "ext-qobuz-song-321" },
                { "f", "json" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.Unstar();

        // Assert
        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(capturedRequest);
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        Assert.Contains("/rest/unstar", requestUrl);
        Assert.Contains("id=2001", requestUrl);
        Assert.DoesNotContain("ext-qobuz-song-321", requestUrl);
    }

    [Fact]
    public async Task Unstar_WithUnresolvableExternalSongId_ReturnsErrorAndDoesNotRelay()
    {
        // Arrange
        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-deezer-song-456"))
            .Returns((true, "deezer", "song", "456"));
        _mockLocalLibraryService
            .Setup(x => x.GetLocalIdForExternalSongAsync("deezer", "456"))
            .ReturnsAsync((string?)null);

        HttpRequestMessage? capturedRequest = null;
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "ext-deezer-song-456" },
                { "f", "xml" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.Unstar();

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Contains("status=\"failed\"", contentResult.Content ?? "");
        Assert.Contains("code=\"70\"", contentResult.Content ?? "");
        Assert.Null(capturedRequest);
    }

    [Fact]
    public async Task Star_InCacheMode_WhenSongIsInCache_PermanentizesAndReturnsSuccess()
    {
        // Arrange
        var settings = Options.Create(new SubsonicSettings
        {
            Url = "http://localhost:4533",
            StorageMode = StorageMode.Cache
        });

        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-deezer-song-789"))
            .Returns((true, "deezer", "song", "789"));
        
        _mockDownloadService
            .Setup(x => x.PermanentizeCachedSongAsync("deezer", "789", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "ext-deezer-song-789" },
                { "f", "xml" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK),
            customSettings: settings);

        // Act
        var result = await controller.Star();

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Contains("status=\"ok\"", contentResult.Content ?? "");
        
        _mockDownloadService.Verify(x => x.PermanentizeCachedSongAsync("deezer", "789", It.IsAny<CancellationToken>()), Times.Once);
        _mockDownloadService.Verify(x => x.DownloadSongToPermanentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Star_InCacheMode_WhenSongNotInCache_StartsDownloadAndReturnsSuccess()
    {
        // Arrange
        var settings = Options.Create(new SubsonicSettings
        {
            Url = "http://localhost:4533",
            StorageMode = StorageMode.Cache
        });

        var downloadCompleted = new ManualResetEventSlim(false);

        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-deezer-song-789"))
            .Returns((true, "deezer", "song", "789"));
        
        _mockDownloadService
            .Setup(x => x.PermanentizeCachedSongAsync("deezer", "789", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Mock the DownloadSongToPermanentAsync to return a value, as it's now awaited in ScheduleDownloadOfStarredSong
        _mockDownloadService
            .Setup(x => x.DownloadSongToPermanentAsync("deezer", "789", It.IsAny<CancellationToken>()))
            .ReturnsAsync("local-id-789")
            .Callback(downloadCompleted.Set);

        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "ext-deezer-song-789" },
                { "f", "xml" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK),
            customSettings: settings);

        // Act
        var result = await controller.Star();

        // Assert
        Assert.True(downloadCompleted.Wait(TimeSpan.FromSeconds(1)),  "Worker did not complete work within timeout.");
        
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Contains("status=\"ok\"", contentResult.Content ?? "");
        
        // Verify that download was scheduled
        _mockDownloadService.Verify(x => x.DownloadSongToPermanentAsync("deezer", "789", It.IsAny<CancellationToken>()), Times.Once);
    }
}
