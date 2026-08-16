using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicControllerGetArtistTests
{
    private const string NavidromeArtistJson =
        "{\"subsonic-response\":{\"status\":\"ok\",\"artist\":{\"id\":\"local-artist-id\",\"name\":\"Genesis\",\"albumCount\":1," +
        "\"album\":[{\"id\":\"local-album-1\",\"name\":\"We Can't Dance\"}]}}}";

    private static SubsonicController CreateController(Mock<IMusicMetadataService> metadataServiceMock)
    {
        var requestParser = new SubsonicRequestParser();
        var responseBuilder = new SubsonicResponseBuilder();
        var modelMapper = new SubsonicModelMapper(
            responseBuilder,
            new Mock<ILogger<SubsonicModelMapper>>().Object);

        var settings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });

        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(NavidromeArtistJson, System.Text.Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var proxyService = new SubsonicProxyService(
            mockHttpClientFactory.Object,
            settings,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var localLibraryServiceMock = new Mock<ILocalLibraryService>();
        localLibraryServiceMock
            .Setup(x => x.ParseSongId(It.IsAny<string>()))
            .Returns((false, null, null));

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
        httpContext.Request.QueryString = new QueryString("?id=local-artist-id&f=json");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private static List<string> GetMergedAlbumNames(IActionResult result)
    {
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        using var doc = JsonDocument.Parse(json);
        var albums = doc.RootElement
            .GetProperty("subsonic-response")
            .GetProperty("artist")
            .GetProperty("album");
        return albums.EnumerateArray()
            .Select(a => a.GetProperty("name").GetString() ?? "")
            .ToList();
    }

    [Fact]
    public async Task GetArtist_WhenTopResultIsHomonym_PicksCandidateMatchingLocalAlbums()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.SearchArtistsAsync("Genesis", It.IsAny<int>()))
            .ReturnsAsync(new List<Artist>
            {
                new Artist { Id = "ext-deezer-artist-1", Name = "Genesis", ExternalProvider = "deezer", ExternalId = "1" },
                new Artist { Id = "ext-deezer-artist-2", Name = "Genesis", ExternalProvider = "deezer", ExternalId = "2" }
            });
        metadataServiceMock
            .Setup(x => x.GetArtistAlbumsAsync("deezer", "1"))
            .ReturnsAsync(new List<Album> { new Album { Id = "ext-deezer-album-11", Title = "Diamante" } });
        metadataServiceMock
            .Setup(x => x.GetArtistAlbumsAsync("deezer", "2"))
            .ReturnsAsync(new List<Album>
            {
                new Album { Id = "ext-deezer-album-21", Title = "We Can't Dance" },
                new Album { Id = "ext-deezer-album-22", Title = "Invisible Touch" }
            });

        var controller = CreateController(metadataServiceMock);

        var result = await controller.GetArtist();

        var albumNames = GetMergedAlbumNames(result);
        Assert.Contains("We Can't Dance", albumNames);
        Assert.Contains("Invisible Touch", albumNames);
        Assert.DoesNotContain("Diamante", albumNames);
    }
}
