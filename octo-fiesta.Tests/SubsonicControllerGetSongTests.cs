using System.Net;
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

namespace octo_fiesta.Tests;

public class SubsonicControllerGetSongTests
{
    private const string ExternalId = "ext-squidwtf-song-4024016711";

    private static SubsonicController CreateController(
        Mock<ILocalLibraryService> localLibraryServiceMock,
        Mock<IMusicMetadataService> metadataServiceMock,
        out List<string> capturedProxyUrls)
    {
        var requestParser = new SubsonicRequestParser();
        var responseBuilder = new SubsonicResponseBuilder();
        var modelMapper = new SubsonicModelMapper(
            responseBuilder,
            new Mock<ILogger<SubsonicModelMapper>>().Object);

        var settings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });

        var urls = new List<string>();
        capturedProxyUrls = urls;

        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => urls.Add(req.RequestUri!.ToString()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"subsonic-response\":{\"status\":\"ok\",\"song\":{\"id\":\"navidrome-real-id\"}}}",
                    System.Text.Encoding.UTF8,
                    "application/json")
            });

        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var proxyService = new SubsonicProxyService(
            mockHttpClientFactory.Object,
            settings,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var controller = new SubsonicController(
            settings,
            metadataServiceMock.Object,
            localLibraryServiceMock.Object,
            new Mock<IDownloadService>().Object,
            requestParser,
            responseBuilder,
            modelMapper,
            proxyService,
            new Mock<IHostApplicationLifetime>().Object,
            new Mock<ILogger<SubsonicController>>().Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString($"?id={ExternalId}&f=json");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    [Fact]
    public async Task GetSong_WhenDownloadedTrackResolvesToLocalId_RelaysToNavidromeWithRealId()
    {
        var localLibraryServiceMock = new Mock<ILocalLibraryService>();
        localLibraryServiceMock
            .Setup(x => x.ParseSongId(It.IsAny<string>()))
            .Returns((true, "squidwtf", "4024016711"));
        localLibraryServiceMock
            .Setup(x => x.GetLocalIdForExternalSongAsync("squidwtf", "4024016711"))
            .ReturnsAsync("navidrome-real-id");

        var metadataServiceMock = new Mock<IMusicMetadataService>();

        var controller = CreateController(localLibraryServiceMock, metadataServiceMock, out var capturedProxyUrls);

        var result = await controller.GetSong();

        Assert.IsType<FileContentResult>(result);
        Assert.Single(capturedProxyUrls);
        Assert.Contains("id=navidrome-real-id", capturedProxyUrls[0]);
        Assert.DoesNotContain("ext-squidwtf", capturedProxyUrls[0]);
        metadataServiceMock.Verify(x => x.GetSongAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
