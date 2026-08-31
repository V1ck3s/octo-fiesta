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

public class SubsonicControllerGetAlbumTests
{
    private const string NavidromeAlbumJson =
        "{\"subsonic-response\":{\"status\":\"ok\",\"album\":{\"id\":\"local-album-1\",\"name\":\"Kammthaar\"," +
        "\"artist\":\"Ultra Vomit\",\"songCount\":1,\"duration\":154,\"song\":[" +
        "{\"id\":\"local-song-1\",\"title\":\"Kammthaar\",\"discNumber\":1,\"track\":1,\"duration\":154,\"playCount\":7}]}}}";

    private const string NavidromeAlbumXml =
        "<subsonic-response status=\"ok\" version=\"1.16.1\" xmlns=\"http://subsonic.org/restapi\">" +
        "<album id=\"local-album-1\" name=\"Kammthaar\" artist=\"Ultra Vomit\" songCount=\"1\" duration=\"154\">" +
        "<song id=\"local-song-1\" title=\"Kammthaar\" discNumber=\"1\" track=\"1\" duration=\"154\" playCount=\"7\" />" +
        "</album></subsonic-response>";

    private static Mock<IMusicMetadataService> CreateMetadataService()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.SearchAlbumsAsync("Ultra Vomit Kammthaar", It.IsAny<int>()))
            .ReturnsAsync(new List<Album>
            {
                new Album
                {
                    Id = "ext-deezer-album-7",
                    Title = "Kammthaar",
                    Artist = "Ultra Vomit",
                    ExternalProvider = "deezer",
                    ExternalId = "7"
                }
            });
        metadataServiceMock
            .Setup(x => x.GetAlbumAsync("deezer", "7"))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-7",
                Title = "Kammthaar",
                Artist = "Ultra Vomit",
                Songs = new List<Song>
                {
                    new Song { Title = "Kammthaar", Track = 1, DiscNumber = 1, Duration = 154 },
                    new Song
                    {
                        Title = "Kammthaar (Live)",
                        Track = 2,
                        DiscNumber = 1,
                        Duration = 200,
                        ExternalProvider = "deezer",
                        ExternalId = "8"
                    }
                }
            });

        return metadataServiceMock;
    }

    private static SubsonicController CreateController(
        Mock<IMusicMetadataService> metadataServiceMock,
        bool asXml,
        string? navidromeJson = null)
    {
        var responseBuilder = new SubsonicResponseBuilder();
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
                Content = new StringContent(
                    asXml ? NavidromeAlbumXml : navidromeJson ?? NavidromeAlbumJson,
                    System.Text.Encoding.UTF8,
                    asXml ? "application/xml" : "application/json")
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
            .Setup(x => x.ParseExternalId(It.IsAny<string>()))
            .Returns((false, null, null, null));

        var controller = new SubsonicController(
            settings,
            metadataServiceMock.Object,
            localLibraryServiceMock.Object,
            new Mock<IDownloadService>().Object,
            new SubsonicRequestParser(),
            responseBuilder,
            new SubsonicModelMapper(responseBuilder, new Mock<ILogger<SubsonicModelMapper>>().Object),
            proxyService,
            new Mock<IHostApplicationLifetime>().Object,
            new Mock<ILogger<SubsonicController>>().Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = asXml
            ? new QueryString("?id=local-album-1")
            : new QueryString("?id=local-album-1&f=json");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    [Fact]
    public async Task GetAlbum_WhenClientAsksJson_AppendsMissingExternalSongs()
    {
        var controller = CreateController(CreateMetadataService(), asXml: false);

        var result = await controller.GetAlbum();

        var jsonResult = Assert.IsType<JsonResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        var album = doc.RootElement.GetProperty("subsonic-response").GetProperty("album");
        var songs = album.GetProperty("song").EnumerateArray().ToList();

        Assert.Equal(2, album.GetProperty("songCount").GetInt32());
        Assert.Equal(354, album.GetProperty("duration").GetInt32());
        Assert.Equal(
            new[] { "Kammthaar", "Kammthaar (Live)" },
            songs.Select(s => s.GetProperty("title").GetString()));
        Assert.Equal("local-song-1", songs[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetAlbum_WhenClientAsksXml_AppendsMissingExternalSongs()
    {
        var controller = CreateController(CreateMetadataService(), asXml: true);

        var result = await controller.GetAlbum();

        var content = Assert.IsType<ContentResult>(result);
        var album = XDocument.Parse(content.Content!).Root!.Elements()
            .Single(e => e.Name.LocalName == "album");
        var songs = album.Elements().Where(e => e.Name.LocalName == "song").ToList();

        Assert.Equal("2", album.Attribute("songCount")?.Value);
        Assert.Equal("354", album.Attribute("duration")?.Value);
        Assert.Equal(
            new[] { "Kammthaar", "Kammthaar (Live)" },
            songs.Select(s => s.Attribute("title")?.Value));
        Assert.Equal("local-song-1", songs[0].Attribute("id")?.Value);

        // Navidrome attributes of the owned track must survive the merge
        Assert.Equal("7", songs[0].Attribute("playCount")?.Value);
    }

    [Fact]
    public async Task GetAlbum_WhenExternalAlbumAddsNothing_ReturnsNavidromePayloadUntouched()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.SearchAlbumsAsync("Ultra Vomit Kammthaar", It.IsAny<int>()))
            .ReturnsAsync(new List<Album>());

        var controller = CreateController(metadataServiceMock, asXml: true);

        var result = await controller.GetAlbum();

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal(NavidromeAlbumXml, System.Text.Encoding.UTF8.GetString(fileResult.FileContents));
    }

    private const string NavidromeVisionsJson =
        "{\"subsonic-response\":{\"status\":\"ok\",\"album\":{\"id\":\"local-album-2\",\"name\":\"Visions\"," +
        "\"artist\":\"Grimes\",\"songCount\":1,\"duration\":251,\"song\":[" +
        "{\"id\":\"local-song-2\",\"title\":\"Oblivion\",\"discNumber\":1,\"track\":4,\"duration\":251}]}}}";

    private static List<string> GetMergedSongTitles(IActionResult result)
    {
        var jsonResult = Assert.IsType<JsonResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        return doc.RootElement
            .GetProperty("subsonic-response")
            .GetProperty("album")
            .GetProperty("song")
            .EnumerateArray()
            .Select(s => s.GetProperty("title").GetString() ?? "")
            .ToList();
    }

    [Fact]
    public async Task GetAlbum_WhenCandidateIsHomonymArtist_KeepsItsSongsOut()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.SearchAlbumsAsync("Grimes Visions", It.IsAny<int>()))
            .ReturnsAsync(new List<Album>
            {
                new Album
                {
                    Id = "ext-deezer-album-149180",
                    Title = "Starhand Visions",
                    Artist = "Gary Grimes",
                    ExternalProvider = "deezer",
                    ExternalId = "149180"
                }
            });

        var controller = CreateController(metadataServiceMock, asXml: false, navidromeJson: NavidromeVisionsJson);

        var result = await controller.GetAlbum();

        Assert.Equal(new[] { "Oblivion" }, GetMergedSongTitles(result));
        metadataServiceMock.Verify(x => x.GetAlbumAsync("deezer", "149180"), Times.Never);
    }

    [Fact]
    public async Task GetAlbum_WhenCandidateOnlyDiffersByEditionSuffix_MergesItsSongs()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.SearchAlbumsAsync("Grimes Visions", It.IsAny<int>()))
            .ReturnsAsync(new List<Album>
            {
                new Album
                {
                    Id = "ext-deezer-album-1545636",
                    Title = "Visions (Deluxe Edition)",
                    Artist = "Grimes",
                    ExternalProvider = "deezer",
                    ExternalId = "1545636"
                }
            });
        metadataServiceMock
            .Setup(x => x.GetAlbumAsync("deezer", "1545636"))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-1545636",
                Title = "Visions (Deluxe Edition)",
                Artist = "Grimes",
                Songs = new List<Song>
                {
                    new Song { Title = "Oblivion", Track = 4, DiscNumber = 1, Duration = 251 },
                    new Song
                    {
                        Title = "Genesis",
                        Track = 5,
                        DiscNumber = 1,
                        Duration = 255,
                        ExternalProvider = "deezer",
                        ExternalId = "15456361"
                    }
                }
            });

        var controller = CreateController(metadataServiceMock, asXml: false, navidromeJson: NavidromeVisionsJson);

        var result = await controller.GetAlbum();

        Assert.Equal(new[] { "Oblivion", "Genesis" }, GetMergedSongTitles(result));
    }

    [Fact]
    public async Task GetAlbum_WhenCandidateArtistNamesAFeaturedCollaborator_MergesItsSongs()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.SearchAlbumsAsync("Grimes Visions", It.IsAny<int>()))
            .ReturnsAsync(new List<Album>
            {
                new Album
                {
                    Id = "ext-deezer-album-1545636",
                    Title = "Visions",
                    Artist = "Grimes feat. Doldrums",
                    ExternalProvider = "deezer",
                    ExternalId = "1545636"
                }
            });
        metadataServiceMock
            .Setup(x => x.GetAlbumAsync("deezer", "1545636"))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-1545636",
                Title = "Visions",
                Artist = "Grimes feat. Doldrums",
                Songs = new List<Song>
                {
                    new Song { Title = "Oblivion", Track = 4, DiscNumber = 1, Duration = 251 },
                    new Song
                    {
                        Title = "Vowels = Space and Time",
                        Track = 6,
                        DiscNumber = 1,
                        Duration = 274,
                        ExternalProvider = "deezer",
                        ExternalId = "15456362"
                    }
                }
            });

        var controller = CreateController(metadataServiceMock, asXml: false, navidromeJson: NavidromeVisionsJson);

        var result = await controller.GetAlbum();

        Assert.Equal(new[] { "Oblivion", "Vowels = Space and Time" }, GetMergedSongTitles(result));
    }

    [Fact]
    public async Task GetAlbum_WhenCandidateTitleKeepsNamingThings_KeepsItsSongsOut()
    {
        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.SearchAlbumsAsync("Grimes Visions", It.IsAny<int>()))
            .ReturnsAsync(new List<Album>
            {
                new Album
                {
                    Id = "ext-deezer-album-999",
                    Title = "Visions Of The Land",
                    Artist = "Grimes",
                    ExternalProvider = "deezer",
                    ExternalId = "999"
                }
            });

        var controller = CreateController(metadataServiceMock, asXml: false, navidromeJson: NavidromeVisionsJson);

        var result = await controller.GetAlbum();

        Assert.Equal(new[] { "Oblivion" }, GetMergedSongTitles(result));
        metadataServiceMock.Verify(x => x.GetAlbumAsync("deezer", "999"), Times.Never);
    }
}
