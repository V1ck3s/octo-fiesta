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
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace octo_fiesta.Tests;

/// <summary>
/// The provider station ("My Wave") is offered as a playlist: listed by getPlaylists, served by
/// getPlaylist, never downloaded by a star, and fed with playback reports from stream and scrobble.
/// </summary>
public class SubsonicControllerStationTests
{
    private readonly Mock<IMusicMetadataService> _mockMetadataService = new();
    private readonly Mock<ILocalLibraryService> _mockLocalLibraryService = new();
    private readonly Mock<IDownloadService> _mockDownloadService = new();
    private readonly Mock<IStationService> _mockStation = new();
    private readonly SubsonicResponseBuilder _responseBuilder = new();
    private readonly IOptions<SubsonicSettings> _settings =
        Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });
    private readonly List<string> _relayedPaths = new();

    private static readonly ExternalPlaylist Wave = new()
    {
        Id = "pl-yandex-wave",
        Name = "Моя волна",
        CuratorName = "Яндекс Музыка",
        Provider = "yandex",
        ExternalId = "wave",
        TrackCount = 20
    };

    public SubsonicControllerStationTests()
    {
        _mockStation.SetupGet(x => x.StationPlaylist).Returns(Wave);
        _mockLocalLibraryService
            .Setup(x => x.ParseSongId(It.IsAny<string>()))
            .Returns((false, null, null));
    }

    private SubsonicController CreateController(
        Dictionary<string, string> queryParams,
        string navidromeBody = "",
        string navidromeContentType = "application/json",
        bool withStation = true)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                _relayedPaths.Add(request.RequestUri!.AbsolutePath);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(navidromeBody) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(navidromeContentType);
                return response;
            });

        var httpClient = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var proxyService = new SubsonicProxyService(
            factory.Object, _settings, new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(x => x.ApplicationStopping).Returns(CancellationToken.None);

        var controller = new SubsonicController(
            _settings,
            _mockMetadataService.Object,
            _mockLocalLibraryService.Object,
            _mockDownloadService.Object,
            new SubsonicRequestParser(),
            _responseBuilder,
            new SubsonicModelMapper(_responseBuilder, new Mock<ILogger<SubsonicModelMapper>>().Object),
            proxyService,
            lifetime.Object,
            new Mock<ILogger<SubsonicController>>().Object,
            playlistSyncService: null,
            stationService: withStation ? _mockStation.Object : null);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?" + string.Join("&", queryParams.Select(kv => $"{kv.Key}={kv.Value}")));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private const string NavidromePlaylists =
        "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\",\"playlists\":{\"playlist\":[{\"id\":\"p1\",\"name\":\"Mine\"}]}}}";

    private static JsonElement JsonOf(IActionResult result)
    {
        var text = result switch
        {
            ContentResult content => content.Content!,
            JsonResult json => JsonSerializer.Serialize(json.Value),
            FileContentResult file => System.Text.Encoding.UTF8.GetString(file.FileContents),
            _ => throw new InvalidOperationException(result.GetType().Name)
        };
        return JsonDocument.Parse(text).RootElement.GetProperty("subsonic-response").Clone();
    }

    [Fact]
    public async Task GetPlaylists_Json_ListsTheStationFirst()
    {
        var controller = CreateController(new() { { "f", "json" } }, NavidromePlaylists);

        var response = JsonOf(await controller.GetPlaylists());

        var names = response.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["Моя волна", "Mine"], names);
        Assert.Equal("pl-yandex-wave", response.GetProperty("playlists").GetProperty("playlist")[0].GetProperty("id").GetString());
    }

    private static readonly ExternalPlaylist Daily = new()
    {
        Id = "pl-yandex-daily-uuid",
        Name = "Плейлист дня",
        CuratorName = "Yandex Music",
        Provider = "yandex",
        ExternalId = "daily-uuid",
        TrackCount = 60
    };

    [Fact]
    public async Task GetPlaylists_ListsTheStationThenPersonalPlaylistsThenTheLibrary()
    {
        _mockMetadataService.Setup(x => x.GetPersonalPlaylistsAsync()).ReturnsAsync(new List<ExternalPlaylist> { Daily });
        var controller = CreateController(new() { { "f", "json" } }, NavidromePlaylists);

        var response = JsonOf(await controller.GetPlaylists());

        var ids = response.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["pl-yandex-wave", "pl-yandex-daily-uuid", "p1"], ids);
    }

    [Fact]
    public async Task GetPlaylists_WithoutStation_StillListsPersonalPlaylists()
    {
        _mockMetadataService.Setup(x => x.GetPersonalPlaylistsAsync()).ReturnsAsync(new List<ExternalPlaylist> { Daily });
        var controller = CreateController(new() { { "f", "json" } }, NavidromePlaylists, withStation: false);

        var response = JsonOf(await controller.GetPlaylists());

        var ids = response.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["pl-yandex-daily-uuid", "p1"], ids);
    }

    [Fact]
    public async Task GetPlaylists_PersonalPlaylistsFailure_KeepsTheStationAndTheLibrary()
    {
        _mockMetadataService.Setup(x => x.GetPersonalPlaylistsAsync()).ThrowsAsync(new HttpRequestException("feed down"));
        var controller = CreateController(new() { { "f", "json" } }, NavidromePlaylists);

        var response = JsonOf(await controller.GetPlaylists());

        var ids = response.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["pl-yandex-wave", "p1"], ids);
    }

    [Fact]
    public async Task GetPlaylists_Xml_ListsTheStationFirst()
    {
        const string xml = "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\">"
                           + "<playlists><playlist id=\"p1\" name=\"Mine\"/></playlists></subsonic-response>";
        var controller = CreateController(new(), xml, "application/xml");

        var content = Assert.IsType<ContentResult>(await controller.GetPlaylists());

        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var ids = XDocument.Parse(content.Content!).Root!.Element(ns + "playlists")!.Elements(ns + "playlist")
            .Select(p => (string)p.Attribute("id")!).ToArray();
        Assert.Equal(["pl-yandex-wave", "p1"], ids);
    }

    [Fact]
    public async Task GetPlaylists_WithoutStation_RelaysUnchanged()
    {
        var controller = CreateController(new() { { "f", "json" } }, NavidromePlaylists, withStation: false);

        var file = Assert.IsType<FileContentResult>(await controller.GetPlaylists());

        Assert.Equal(NavidromePlaylists, System.Text.Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task GetPlaylist_Station_ServesTheNextTracksWithoutNavidrome()
    {
        _mockMetadataService.Setup(x => x.GetPlaylistAsync("yandex", "wave")).ReturnsAsync(Wave);
        _mockMetadataService.Setup(x => x.GetPlaylistTracksAsync("yandex", "wave")).ReturnsAsync(new List<Song>
        {
            new() { Title = "Peremen", Artist = "Kino", ExternalProvider = "yandex", ExternalId = "1:10", IsLocal = false },
            new() { Title = "Kukushka", Artist = "Kino", ExternalProvider = "yandex", ExternalId = "2:20", IsLocal = false }
        });
        var controller = CreateController(new() { { "id", "pl-yandex-wave" }, { "f", "json" } });

        var playlist = JsonOf(await controller.GetPlaylist()).GetProperty("playlist");

        Assert.Equal("Моя волна", playlist.GetProperty("name").GetString());
        Assert.Equal(["Peremen", "Kukushka"],
            playlist.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("title").GetString()!).ToArray());
        Assert.Empty(_relayedPaths);
    }

    [Fact]
    public async Task GetPlaylist_LibraryPlaylist_RelaysToNavidrome()
    {
        var controller = CreateController(new() { { "id", "p1" }, { "f", "json" } }, "{\"subsonic-response\":{\"status\":\"ok\"}}");

        Assert.IsType<FileContentResult>(await controller.GetPlaylist());

        Assert.Contains(_relayedPaths, p => p.EndsWith("/rest/getPlaylist"));
    }

    [Fact]
    public async Task Star_OnTheStation_DownloadsNothing()
    {
        var controller = CreateController(new() { { "albumId", "pl-yandex-wave" }, { "f", "json" } });

        var response = JsonOf(await controller.Star());

        Assert.Equal("ok", response.GetProperty("status").GetString());
        Assert.Empty(_relayedPaths);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task Scrobble_OfAnExternalSong_ReachesTheStation(string submission, bool finished)
    {
        _mockLocalLibraryService.Setup(x => x.ParseSongId("ext-yandex-song-1:10")).Returns((true, "yandex", "1:10"));
        var controller = CreateController(new() { { "id", "ext-yandex-song-1:10" }, { "submission", submission }, { "f", "json" } });

        await controller.Scrobble();

        if (finished)
        {
            _mockStation.Verify(x => x.TrackFinished("yandex", "1:10"), Times.Once);
            _mockStation.Verify(x => x.TrackStarted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        else
        {
            _mockStation.Verify(x => x.TrackStarted("yandex", "1:10"), Times.Once);
            _mockStation.Verify(x => x.TrackFinished(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }

    [Fact]
    public async Task Stream_OfAnExternalSong_ReportsItsStart()
    {
        _mockLocalLibraryService.Setup(x => x.ParseSongId("ext-yandex-song-1:10")).Returns((true, "yandex", "1:10"));
        _mockLocalLibraryService.Setup(x => x.GetLocalIdForExternalSongAsync("yandex", "1:10")).ReturnsAsync("local-1");
        var controller = CreateController(new() { { "id", "ext-yandex-song-1:10" } }, "audio", "audio/mpeg");

        await controller.Stream();

        _mockStation.Verify(x => x.TrackStarted("yandex", "1:10"), Times.Once);
    }
}
