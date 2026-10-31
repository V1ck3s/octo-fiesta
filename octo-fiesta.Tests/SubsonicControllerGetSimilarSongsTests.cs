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
using System.Net;
using System.Text.Json;

namespace octo_fiesta.Tests;

/// <summary>
/// getSimilarSongs / getSimilarSongs2 (Instant Mix, Song Radio) used to fall through to the
/// catch-all proxy, so Navidrome answered from the local library with its similarity agent
/// only. These tests pin the merge with the provider's recommendations.
/// </summary>
public class SubsonicControllerGetSimilarSongsTests
{
    private readonly Mock<IMusicMetadataService> _mockMetadataService;
    private readonly Mock<ILocalLibraryService> _mockLocalLibraryService;
    private readonly Mock<IDownloadService> _mockDownloadService;
    private readonly Mock<ILogger<SubsonicController>> _mockLogger;
    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicModelMapper _modelMapper;
    private readonly IOptions<SubsonicSettings> _settings;
    private readonly List<string> _relayedPaths = new();

    public SubsonicControllerGetSimilarSongsTests()
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

        // library IDs by default; tests with an external seed override this
        _mockLocalLibraryService
            .Setup(x => x.ParseSongId(It.IsAny<string>()))
            .Returns((false, null, null));
    }

    private SubsonicController CreateController(
        Dictionary<string, string> queryParams,
        Dictionary<string, string> navidromeBodies)
    {
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                _relayedPaths.Add(path);
                var body = navidromeBodies.FirstOrDefault(kv => path.EndsWith(kv.Key)).Value
                           ?? "{\"subsonic-response\":{\"status\":\"failed\",\"version\":\"1.16.1\"}}";
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return response;
            });

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

        var httpContext = new DefaultHttpContext();
        var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        httpContext.Request.QueryString = new QueryString("?" + queryString);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    private static string NavidromeSimilar(string resultName, params (string Id, string Title, string Artist)[] songs)
    {
        var entries = songs.Select(s =>
            $"{{\"id\":\"{s.Id}\",\"title\":\"{s.Title}\",\"artist\":\"{s.Artist}\"}}");
        return "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\","
               + $"\"{resultName}\":{{\"song\":[" + string.Join(",", entries) + "]}}}";
    }

    private static string NavidromeSong(string id, string title, string artist) =>
        "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\","
        + $"\"song\":{{\"id\":\"{id}\",\"title\":\"{title}\",\"artist\":\"{artist}\"}}}}}}";

    private static Song External(string title, string artist = "Kino", string? id = null) => new()
    {
        Title = title,
        Artist = artist,
        Album = "Gruppa krovi",
        ExternalProvider = "yandex",
        ExternalId = id ?? title.GetHashCode().ToString("x"),
        IsLocal = false
    };

    private static List<string> TitlesOf(IActionResult result, string resultName = "similarSongs2")
    {
        var json = Assert.IsType<JsonResult>(result);
        using var doc = JsonSerializer.SerializeToDocument(json.Value!);
        var songs = doc.RootElement
            .GetProperty("subsonic-response")
            .GetProperty(resultName)
            .GetProperty("song");

        return songs.EnumerateArray()
            .Select(s => s.GetProperty("title").GetString() ?? "")
            .ToList();
    }

    [Fact]
    public async Task GetSimilarSongs2_ExternalSeed_ReturnsProviderSongsWithoutRelay()
    {
        _mockLocalLibraryService
            .Setup(x => x.ParseSongId("ext-yandex-song-1:10"))
            .Returns((true, "yandex", "1:10"));
        _mockMetadataService
            .Setup(x => x.GetSimilarSongsAsync("yandex", "1:10", 50))
            .ReturnsAsync(new List<Song> { External("Peremen"), External("Kukushka") });

        var controller = CreateController(
            new Dictionary<string, string> { { "id", "ext-yandex-song-1:10" }, { "f", "json" } },
            new Dictionary<string, string>());

        var titles = TitlesOf(await controller.GetSimilarSongs2());

        Assert.Equal(["Peremen", "Kukushka"], titles);
        Assert.Empty(_relayedPaths);
    }

    [Fact]
    public async Task GetSimilarSongs2_LocalSeedWithMapping_InterleavesLocalAndProvider()
    {
        _mockLocalLibraryService
            .Setup(x => x.GetMappingForLocalIdAsync("local-seed"))
            .ReturnsAsync(new LocalSongMapping { ExternalProvider = "yandex", ExternalId = "5:50" });
        _mockMetadataService
            .Setup(x => x.GetSimilarSongsAsync("yandex", "5:50", 50))
            .ReturnsAsync(new List<Song> { External("P1"), External("P2"), External("P3") });

        var controller = CreateController(
            new Dictionary<string, string> { { "id", "local-seed" }, { "f", "json" } },
            new Dictionary<string, string>
            {
                ["getSimilarSongs2"] = NavidromeSimilar("similarSongs2", ("l1", "L1", "Kino"), ("l2", "L2", "Kino"))
            });

        var titles = TitlesOf(await controller.GetSimilarSongs2());

        Assert.Equal(["L1", "P1", "L2", "P2", "P3"], titles);
    }

    [Fact]
    public async Task GetSimilarSongs2_LeavesOutTheSeedNavidromeReturns()
    {
        _mockLocalLibraryService
            .Setup(x => x.GetMappingForLocalIdAsync("local-seed"))
            .ReturnsAsync(new LocalSongMapping { ExternalProvider = "yandex", ExternalId = "5" });
        _mockMetadataService
            .Setup(x => x.GetSimilarSongsAsync("yandex", "5", 50))
            .ReturnsAsync(new List<Song> { External("P1") });

        var controller = CreateController(
            new Dictionary<string, string> { { "id", "local-seed" }, { "f", "json" } },
            new Dictionary<string, string>
            {
                ["getSimilarSongs2"] = NavidromeSimilar("similarSongs2", ("local-seed", "Seed", "Kino"), ("l1", "L1", "Kino"))
            });

        var titles = TitlesOf(await controller.GetSimilarSongs2());

        Assert.Equal(["L1", "P1"], titles);
    }

    [Fact]
    public async Task GetSimilarSongs2_DropsProviderSongsAlreadyInLibraryAndRespectsCount()
    {
        _mockLocalLibraryService
            .Setup(x => x.GetMappingForLocalIdAsync("local-seed"))
            .ReturnsAsync(new LocalSongMapping { ExternalProvider = "yandex", ExternalId = "5" });
        _mockMetadataService
            .Setup(x => x.GetSimilarSongsAsync("yandex", "5", 3))
            .ReturnsAsync(new List<Song> { External("Peremen"), External("Kukushka"), External("Sledi za soboi") });

        var controller = CreateController(
            new Dictionary<string, string> { { "id", "local-seed" }, { "count", "3" }, { "f", "json" } },
            new Dictionary<string, string>
            {
                ["getSimilarSongs2"] = NavidromeSimilar("similarSongs2", ("l1", "PEREMEN", "KINO"))
            });

        var titles = TitlesOf(await controller.GetSimilarSongs2());

        // "PEREMEN" by "KINO" is the same song as the provider's "Peremen" by "Kino"
        Assert.Equal(["PEREMEN", "Kukushka", "Sledi za soboi"], titles);
    }

    [Fact]
    public async Task GetSimilarSongs2_LocalSeedWithoutMapping_ResolvesByTags()
    {
        _mockLocalLibraryService
            .Setup(x => x.GetMappingForLocalIdAsync("local-seed"))
            .ReturnsAsync((LocalSongMapping?)null);
        _mockMetadataService
            .Setup(x => x.SearchSongsAsync("Kino Gruppa krovi", It.IsAny<int>()))
            .ReturnsAsync(new List<Song>
            {
                External("Gruppa krovi", artist: "Kino Tribute Band", id: "8"),
                External("Gruppa krovi", id: "9")
            });
        _mockMetadataService
            .Setup(x => x.GetSimilarSongsAsync("yandex", "9", 50))
            .ReturnsAsync(new List<Song> { External("Peremen") });

        var controller = CreateController(
            new Dictionary<string, string> { { "id", "local-seed" }, { "f", "json" } },
            new Dictionary<string, string>
            {
                ["getSimilarSongs2"] = NavidromeSimilar("similarSongs2"),
                ["getSong"] = NavidromeSong("local-seed", "Gruppa krovi", "Kino")
            });

        var titles = TitlesOf(await controller.GetSimilarSongs2());

        Assert.Equal(["Peremen"], titles);
    }

    [Fact]
    public async Task GetSimilarSongs2_NoProviderSongs_RelaysNavidromeUnchanged()
    {
        _mockLocalLibraryService
            .Setup(x => x.GetMappingForLocalIdAsync("local-seed"))
            .ReturnsAsync(new LocalSongMapping { ExternalProvider = "yandex", ExternalId = "5" });
        _mockMetadataService
            .Setup(x => x.GetSimilarSongsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        var navidromeBody = NavidromeSimilar("similarSongs2", ("l1", "L1", "Kino"));
        var controller = CreateController(
            new Dictionary<string, string> { { "id", "local-seed" }, { "f", "json" } },
            new Dictionary<string, string> { ["getSimilarSongs2"] = navidromeBody });

        var result = await controller.GetSimilarSongs2();

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(navidromeBody, System.Text.Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task GetSimilarSongs_UsesTheNonId3ResultElement()
    {
        _mockLocalLibraryService
            .Setup(x => x.ParseSongId("ext-yandex-song-1"))
            .Returns((true, "yandex", "1"));
        _mockMetadataService
            .Setup(x => x.GetSimilarSongsAsync("yandex", "1", 50))
            .ReturnsAsync(new List<Song> { External("Peremen") });

        var controller = CreateController(
            new Dictionary<string, string> { { "id", "ext-yandex-song-1" }, { "f", "json" } },
            new Dictionary<string, string>());

        var titles = TitlesOf(await controller.GetSimilarSongs(), "similarSongs");

        Assert.Equal(["Peremen"], titles);
    }

    [Fact]
    public async Task GetSimilarSongs2_MissingId_ReturnsError()
    {
        var controller = CreateController(
            new Dictionary<string, string> { { "f", "json" } },
            new Dictionary<string, string>());

        var result = await controller.GetSimilarSongs2();

        var json = Assert.IsType<JsonResult>(result);
        using var doc = JsonSerializer.SerializeToDocument(json.Value!);
        Assert.Equal("failed", doc.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
    }
}
