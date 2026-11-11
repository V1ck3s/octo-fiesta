using System.Text.Json;
using System.Xml.Linq;
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

public class SubsonicControllerItemInfoTests
{
    private static SubsonicController CreateController(
        Mock<ILocalLibraryService> localLibraryServiceMock,
        Mock<IMusicMetadataService> metadataServiceMock,
        string query,
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
                    "{\"subsonic-response\":{\"status\":\"ok\",\"albumInfo\":{\"notes\":\"from navidrome\"}}}",
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
        httpContext.Request.QueryString = new QueryString(query);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private static Mock<ILocalLibraryService> LibraryMock(bool isExternal, string type)
    {
        var mock = new Mock<ILocalLibraryService>();
        mock.Setup(x => x.ParseExternalId(It.IsAny<string>()))
            .Returns(isExternal ? (true, "qobuz", type, "123") : (false, null, null, null));
        return mock;
    }

    [Fact]
    public async Task GetAlbumInfo2_ExternalAlbum_ReturnsEmptyInfoWithoutRelaying()
    {
        var controller = CreateController(LibraryMock(true, "album"), new Mock<IMusicMetadataService>(),
            "?id=ext-qobuz-album-123&f=json", out var capturedProxyUrls);

        var result = await controller.GetAlbumInfo2();

        var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value));
        var response = json.RootElement.GetProperty("subsonic-response");
        Assert.Equal("ok", response.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, response.GetProperty("albumInfo").ValueKind);
        Assert.Empty(capturedProxyUrls);
    }

    [Fact]
    public async Task GetArtistInfo2_ExternalArtist_Xml_ReturnsEmptyInfoElement()
    {
        var controller = CreateController(LibraryMock(true, "artist"), new Mock<IMusicMetadataService>(),
            "?id=ext-qobuz-artist-123", out var capturedProxyUrls);

        var result = await controller.GetArtistInfo2();

        var doc = XDocument.Parse(Assert.IsType<ContentResult>(result).Content!);
        Assert.Equal("ok", doc.Root!.Attribute("status")!.Value);
        Assert.NotNull(doc.Root.Elements().SingleOrDefault(e => e.Name.LocalName == "artistInfo2"));
        Assert.Empty(capturedProxyUrls);
    }

    [Fact]
    public async Task GetAlbumInfo2_LocalAlbum_RelaysToBackingServer()
    {
        var controller = CreateController(LibraryMock(false, "album"), new Mock<IMusicMetadataService>(),
            "?id=navidrome-album-id&f=json", out var capturedProxyUrls);

        var result = await controller.GetAlbumInfo2();

        Assert.IsType<FileContentResult>(result);
        Assert.Single(capturedProxyUrls);
        Assert.Contains("getAlbumInfo2", capturedProxyUrls[0]);
        Assert.Contains("id=navidrome-album-id", capturedProxyUrls[0]);
    }
}
