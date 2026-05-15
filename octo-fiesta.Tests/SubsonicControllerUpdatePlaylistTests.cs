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

public class SubsonicControllerUpdatePlaylistTests
{
    private readonly Mock<IMusicMetadataService> _mockMetadataService;
    private readonly Mock<ILocalLibraryService> _mockLocalLibraryService;
    private readonly Mock<IDownloadService> _mockDownloadService;
    private readonly Mock<ILogger<SubsonicController>> _mockLogger;
    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicModelMapper _modelMapper;
    private readonly IOptions<SubsonicSettings> _settings;

    public SubsonicControllerUpdatePlaylistTests()
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
        Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null,
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
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => responseFactory?.Invoke(req) ?? proxyResponse);

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
    public async Task UpdatePlaylist_WithResolvableExternalSongId_UsesLocalId()
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
                { "playlistId", "42" },
                { "songIdToAdd", "ext-qobuz-song-123" },
                { "f", "json" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.UpdatePlaylist();

        // Assert
        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(capturedRequest);
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        Assert.Contains("/rest/updatePlaylist", requestUrl);
        Assert.Contains("songIdToAdd=9981", requestUrl);
        Assert.DoesNotContain("ext-qobuz-song-123", requestUrl);

        _mockDownloadService.Verify(
            x => x.DownloadSongAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockLocalLibraryService.Verify(x => x.WaitForLocalIdAfterScanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylist_WithExternalSongId_NotInCache_DownloadsToPermanent()
    {
        // Arrange
        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-deezer-song-456"))
            .Returns((true, "deezer", "song", "456"));
        _mockLocalLibraryService
            .SetupSequence(x => x.GetLocalIdForExternalSongAsync("deezer", "456"))
            .ReturnsAsync((string?)null)
            .ReturnsAsync("777");

        _mockDownloadService
            .Setup(x => x.PermanentizeCachedSongAsync("deezer", "456", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockDownloadService
            .Setup(x => x.DownloadSongToPermanentAsync("deezer", "456", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/downloads/song.flac");
        _mockLocalLibraryService
            .Setup(x => x.WaitForLocalIdAfterScanAsync("deezer", "456", It.IsAny<CancellationToken>()))
            .ReturnsAsync("777");

        HttpRequestMessage? capturedRequest = null;
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "playlistId", "42" },
                { "songIdToAdd", "ext-deezer-song-456" },
                { "f", "json" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.UpdatePlaylist();

        // Assert
        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(capturedRequest);
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        Assert.Contains("/rest/updatePlaylist", requestUrl);
        Assert.Contains("songIdToAdd=777", requestUrl);

        _mockDownloadService.Verify(
            x => x.PermanentizeCachedSongAsync("deezer", "456", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockDownloadService.Verify(
            x => x.DownloadSongToPermanentAsync("deezer", "456", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockDownloadService.Verify(
            x => x.DownloadSongAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
        Times.Never);
        _mockLocalLibraryService.Verify(x => x.WaitForLocalIdAfterScanAsync("deezer", "456", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylist_WithExternalSongId_AlreadyInCache_PermanentizesInsteadOfDownloading()
    {
        // Arrange
        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId("ext-deezer-song-456"))
            .Returns((true, "deezer", "song", "456"));
        _mockLocalLibraryService
            .SetupSequence(x => x.GetLocalIdForExternalSongAsync("deezer", "456"))
            .ReturnsAsync((string?)null)
            .ReturnsAsync("777");

        _mockDownloadService
            .Setup(x => x.PermanentizeCachedSongAsync("deezer", "456", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockLocalLibraryService
            .Setup(x => x.WaitForLocalIdAfterScanAsync("deezer", "456", It.IsAny<CancellationToken>()))
            .ReturnsAsync("777");

        HttpRequestMessage? capturedRequest = null;
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "playlistId", "42" },
                { "songIdToAdd", "ext-deezer-song-456" },
                { "f", "json" }
            },
            proxyResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            },
            captureRequest: req => capturedRequest = req);

        // Act
        var result = await controller.UpdatePlaylist();

        // Assert
        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(capturedRequest);
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        Assert.Contains("/rest/updatePlaylist", requestUrl);
        Assert.Contains("songIdToAdd=777", requestUrl);

        _mockDownloadService.Verify(
            x => x.PermanentizeCachedSongAsync("deezer", "456", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockDownloadService.Verify(
            x => x.DownloadSongToPermanentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockDownloadService.Verify(
            x => x.DownloadSongAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockLocalLibraryService.Verify(x => x.WaitForLocalIdAfterScanAsync("deezer", "456", It.IsAny<CancellationToken>()), Times.Once);
    }

}
