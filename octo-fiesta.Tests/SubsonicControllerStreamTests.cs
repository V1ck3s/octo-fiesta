using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicControllerStreamTests
{
    private static SubsonicController CreateController(
        Mock<ILocalLibraryService> localLibraryServiceMock,
        Mock<IDownloadService> downloadServiceMock,
        IHostApplicationLifetime hostApplicationLifetime,
        CancellationToken requestAbortedToken)
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        var requestParser = new SubsonicRequestParser();
        var responseBuilder = new SubsonicResponseBuilder();
        var modelMapper = new SubsonicModelMapper(
            responseBuilder,
            new Mock<ILogger<SubsonicModelMapper>>().Object);

        var settings = Options.Create(new SubsonicSettings
        {
            Url = "http://localhost:4533"
        });

        var mockHttpHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };

        var proxyService = new SubsonicProxyService(
            mockHttpClientFactory.Object,
            settings,
            httpContextAccessor);

        var controller = new SubsonicController(
            settings,
            metadataServiceMock.Object,
            localLibraryServiceMock.Object,
            downloadServiceMock.Object,
            requestParser,
            responseBuilder,
            modelMapper,
            proxyService,
            hostApplicationLifetime,
            new Mock<ILogger<SubsonicController>>().Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?id=ext-deezer-song-123");
        httpContext.RequestAborted = requestAbortedToken;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    [Fact]
    public async Task Stream_WithExternalSong_UsesLinkedCancelableTokenForDownload()
    {
        var localLibraryServiceMock = new Mock<ILocalLibraryService>();
        localLibraryServiceMock
            .Setup(x => x.ParseSongId(It.IsAny<string>()))
            .Returns((true, "deezer", "123"));

        var downloadServiceMock = new Mock<IDownloadService>();
        CancellationToken capturedToken = default;
        downloadServiceMock
            .Setup(x => x.DownloadAndStreamAsync("deezer", "123", It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, _, token) => capturedToken = token)
            .ReturnsAsync(new MemoryStream([1, 2, 3]));

        var appStoppingCts = new CancellationTokenSource();
        var hostLifetimeMock = new Mock<IHostApplicationLifetime>();
        hostLifetimeMock.SetupGet(x => x.ApplicationStopping).Returns(appStoppingCts.Token);

        var controller = CreateController(
            localLibraryServiceMock,
            downloadServiceMock,
            hostLifetimeMock.Object,
            CancellationToken.None);

        var result = await controller.Stream();

        Assert.IsType<FileStreamResult>(result);
        Assert.True(capturedToken.CanBeCanceled);
    }

    [Fact]
    public async Task Stream_WhenApplicationStoppingTokenIsCanceled_PassesCanceledTokenToDownload()
    {
        var localLibraryServiceMock = new Mock<ILocalLibraryService>();
        localLibraryServiceMock
            .Setup(x => x.ParseSongId(It.IsAny<string>()))
            .Returns((true, "deezer", "123"));

        var downloadServiceMock = new Mock<IDownloadService>();
        downloadServiceMock
            .Setup(x => x.DownloadAndStreamAsync("deezer", "123", It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<Stream>(new MemoryStream(new byte[] { 1 }));
            });

        var appStoppingCts = new CancellationTokenSource();
        appStoppingCts.Cancel();

        var hostLifetimeMock = new Mock<IHostApplicationLifetime>();
        hostLifetimeMock.SetupGet(x => x.ApplicationStopping).Returns(appStoppingCts.Token);
        hostLifetimeMock.SetupGet(x => x.ApplicationStarted).Returns(CancellationToken.None);
        hostLifetimeMock.SetupGet(x => x.ApplicationStopped).Returns(CancellationToken.None);

        var controller = CreateController(
            localLibraryServiceMock,
            downloadServiceMock,
            hostLifetimeMock.Object,
            CancellationToken.None);

        var result = await controller.Stream();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        downloadServiceMock.Verify(
            x => x.DownloadAndStreamAsync("deezer", "123", It.Is<CancellationToken>(t => t.IsCancellationRequested)),
            Times.Once);
    }
}
