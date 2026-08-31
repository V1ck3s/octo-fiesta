using System.Net;
using System.Text.Json;
using System.Xml.Linq;
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

    private const string NavidromeArtistXml =
        "<subsonic-response status=\"ok\" version=\"1.16.1\" xmlns=\"http://subsonic.org/restapi\">" +
        "<artist id=\"local-artist-id\" name=\"Genesis\" albumCount=\"1\" sortName=\"genesis\">" +
        "<album id=\"local-album-1\" name=\"We Can't Dance\" songCount=\"12\" playCount=\"3\" />" +
        "</artist></subsonic-response>";

    private const string EmptyNavidromeSearchJson =
        "{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{}}}";

    private const string NavidromeSearchArtistJson =
        "{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{\"artist\":[" +
        "{\"id\":\"other-artist-id\",\"name\":\"Genesis Tribute\",\"albumCount\":9}," +
        "{\"id\":\"local-artist-id\",\"name\":\"Genesis\",\"albumCount\":1}]}}}";

    private static SubsonicController CreateController(
        Mock<IMusicMetadataService> metadataServiceMock,
        string requestedId = "local-artist-id",
        (bool IsExternal, string? Provider, string? ExternalId) parsedId = default,
        string? navidromeSearchJson = null,
        bool navidromeXml = false)
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
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var isSearch = request.RequestUri!.AbsolutePath.Contains("search3");
                var isXml = navidromeXml && !isSearch;
                var body = isSearch
                    ? navidromeSearchJson ?? EmptyNavidromeSearchJson
                    : isXml ? NavidromeArtistXml : NavidromeArtistJson;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, isXml ? "application/xml" : "application/json")
                };
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
            .Returns(parsedId);

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
        httpContext.Request.QueryString = navidromeXml
            ? new QueryString($"?id={requestedId}")
            : new QueryString($"?id={requestedId}&f=json");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private static List<string> GetMergedAlbumNames(IActionResult result)
        => GetMergedAlbums(result).Select(a => a.Name).ToList();

    private static List<(string Id, string Name)> GetMergedAlbums(IActionResult result)
    {
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        using var doc = JsonDocument.Parse(json);
        var albums = doc.RootElement
            .GetProperty("subsonic-response")
            .GetProperty("artist")
            .GetProperty("album");
        return albums.EnumerateArray()
            .Select(a => (
                a.GetProperty("id").GetString() ?? "",
                a.GetProperty("name").GetString() ?? ""))
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

    [Fact]
    public async Task GetArtist_WhenExternalArtistExistsLocally_MergesOwnedAlbumsFirst()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.GetArtistAsync("deezer", "42"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-42", Name = "Genesis" });
        metadataServiceMock
            .Setup(x => x.GetArtistAlbumsAsync("deezer", "42"))
            .ReturnsAsync(new List<Album>
            {
                new Album { Id = "ext-deezer-album-21", Title = "We Can't Dance" },
                new Album { Id = "ext-deezer-album-22", Title = "Invisible Touch" }
            });

        var controller = CreateController(
            metadataServiceMock,
            "ext-deezer-artist-42",
            (true, "deezer", "42"),
            NavidromeSearchArtistJson);

        var result = await controller.GetArtist();

        var albums = GetMergedAlbums(result);
        Assert.Equal(new[] { "We Can't Dance", "Invisible Touch" }, albums.Select(a => a.Name));

        // The owned copy must win, otherwise playing it would download the album again
        Assert.Equal("local-album-1", albums[0].Id);
        Assert.Equal("ext-deezer-album-22", albums[1].Id);
    }

    [Fact]
    public async Task GetArtist_WhenExternalArtistIsUnknownLocally_KeepsProviderAlbumsOnly()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.GetArtistAsync("deezer", "42"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-42", Name = "Genesis" });
        metadataServiceMock
            .Setup(x => x.GetArtistAlbumsAsync("deezer", "42"))
            .ReturnsAsync(new List<Album> { new Album { Id = "ext-deezer-album-21", Title = "Invisible Touch" } });

        var controller = CreateController(
            metadataServiceMock,
            "ext-deezer-artist-42",
            (true, "deezer", "42"));

        var result = await controller.GetArtist();

        var albums = GetMergedAlbums(result);
        Assert.Equal("ext-deezer-album-21", Assert.Single(albums).Id);
    }

    [Fact]
    public async Task GetArtist_WhenClientAsksXml_AppendsExternalAlbumsToNavidromeArtist()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.SearchArtistsAsync("Genesis", It.IsAny<int>()))
            .ReturnsAsync(new List<Artist>
            {
                new Artist { Id = "ext-deezer-artist-1", Name = "Genesis", ExternalProvider = "deezer", ExternalId = "1" }
            });
        metadataServiceMock
            .Setup(x => x.GetArtistAlbumsAsync("deezer", "1"))
            .ReturnsAsync(new List<Album>
            {
                new Album { Id = "ext-deezer-album-11", Title = "We Can't Dance" },
                new Album { Id = "ext-deezer-album-12", Title = "Invisible Touch" }
            });

        var controller = CreateController(metadataServiceMock, navidromeXml: true);

        var result = await controller.GetArtist();

        var content = Assert.IsType<ContentResult>(result);
        var artist = XDocument.Parse(content.Content!).Root!.Elements()
            .Single(e => e.Name.LocalName == "artist");
        var albums = artist.Elements().Where(e => e.Name.LocalName == "album").ToList();

        Assert.Equal("2", artist.Attribute("albumCount")?.Value);
        Assert.Equal(new[] { "We Can't Dance", "Invisible Touch" }, albums.Select(a => a.Attribute("name")?.Value));
        Assert.Equal(new[] { "local-album-1", "ext-deezer-album-12" }, albums.Select(a => a.Attribute("id")?.Value));

        // Navidrome attributes of the owned album must survive the merge
        Assert.Equal("3", albums[0].Attribute("playCount")?.Value);
        Assert.Equal("12", albums[0].Attribute("songCount")?.Value);
    }
}
