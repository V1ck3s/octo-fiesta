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
/// getTopSongs used to fall through to the catch-all proxy, so Navidrome answered
/// alone: it reads the Last.fm ranking then keeps only the titles already in the
/// library. On a sparse library that returns one or two songs even when the
/// provider has the whole catalogue. These tests pin the merge that fixes it.
/// </summary>
public class SubsonicControllerGetTopSongsTests
{
    private readonly Mock<IMusicMetadataService> _mockMetadataService;
    private readonly Mock<ILocalLibraryService> _mockLocalLibraryService;
    private readonly Mock<IDownloadService> _mockDownloadService;
    private readonly Mock<ILogger<SubsonicController>> _mockLogger;
    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicModelMapper _modelMapper;
    private readonly IOptions<SubsonicSettings> _settings;

    public SubsonicControllerGetTopSongsTests()
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
        HttpResponseMessage proxyResponse)
    {
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(proxyResponse);

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

    private static HttpResponseMessage NavidromeTopSongs(params (string Id, string Title)[] songs)
    {
        var entries = songs.Select(s =>
            $"{{\"id\":\"{s.Id}\",\"title\":\"{s.Title}\",\"artist\":\"Serge Gainsbourg\"}}");
        var body = "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\","
                 + "\"topSongs\":{\"song\":[" + string.Join(",", entries) + "]}}}";

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return response;
    }

    private static Song External(string title, string artist = "Serge Gainsbourg") => new()
    {
        Title = title,
        Artist = artist,
        Album = "Histoire de Melody Nelson",
        ExternalProvider = "qobuz",
        ExternalId = title.GetHashCode().ToString("x"),
        IsLocal = false
    };

    private static List<string> TitlesOf(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        using var doc = JsonSerializer.SerializeToDocument(json.Value!);
        var songs = doc.RootElement
            .GetProperty("subsonic-response")
            .GetProperty("topSongs")
            .GetProperty("song");

        return songs.EnumerateArray()
            .Select(s => s.GetProperty("title").GetString() ?? "")
            .ToList();
    }

    [Fact]
    public async Task GetTopSongs_AddsProviderSongsWhenLibraryIsSparse()
    {
        _mockMetadataService
            .Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Song>
            {
                External("Ballade de Melody Nelson"),
                External("Initials B.B."),
                External("La javanaise")
            });

        var controller = CreateController(
            new Dictionary<string, string>
            {
                { "artist", "Serge Gainsbourg" },
                { "count", "50" },
                { "f", "json" }
            },
            NavidromeTopSongs(("1", "Je Suis Venu Te Dire Que Je M'en Vais")));

        var titles = TitlesOf(await controller.GetTopSongs());

        Assert.Equal(4, titles.Count);
        Assert.Equal("Je Suis Venu Te Dire Que Je M'en Vais", titles[0]);
        Assert.Contains("La javanaise", titles);
    }

    [Fact]
    public async Task GetTopSongs_ExcludesSongsFromAnotherArtist()
    {
        // Searching the provider for "Serge Gainsbourg" also surfaces Charlotte
        // Gainsbourg: the artist name has to be checked, not just the query.
        _mockMetadataService
            .Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Song>
            {
                External("Initials B.B."),
                External("Deadly Valentine", artist: "Charlotte Gainsbourg")
            });

        var controller = CreateController(
            new Dictionary<string, string>
            {
                { "artist", "Serge Gainsbourg" },
                { "f", "json" }
            },
            NavidromeTopSongs());

        var titles = TitlesOf(await controller.GetTopSongs());

        Assert.Contains("Initials B.B.", titles);
        Assert.DoesNotContain("Deadly Valentine", titles);
    }

    [Fact]
    public async Task GetTopSongs_KeepsCollaborationsWhereArtistLeadsTheCredit()
    {
        _mockMetadataService
            .Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Song>
            {
                External("Je t'aime moi non plus", artist: "Serge Gainsbourg & Jane Birkin")
            });

        var controller = CreateController(
            new Dictionary<string, string>
            {
                { "artist", "Serge Gainsbourg" },
                { "f", "json" }
            },
            NavidromeTopSongs());

        var titles = TitlesOf(await controller.GetTopSongs());

        Assert.Contains("Je t'aime moi non plus", titles);
    }

    [Fact]
    public async Task GetTopSongs_DoesNotDuplicateATitleAlreadyInTheLibrary()
    {
        // Same song, different casing and a curly apostrophe: StringNormalizer
        // is what keeps it from showing up twice.
        _mockMetadataService
            .Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Song>
            {
                External("je suis venu te dire que je m’en vais"),
                External("La javanaise")
            });

        var controller = CreateController(
            new Dictionary<string, string>
            {
                { "artist", "Serge Gainsbourg" },
                { "f", "json" }
            },
            NavidromeTopSongs(("1", "Je Suis Venu Te Dire Que Je M'en Vais")));

        var titles = TitlesOf(await controller.GetTopSongs());

        Assert.Equal(2, titles.Count);
        Assert.Equal("Je Suis Venu Te Dire Que Je M'en Vais", titles[0]);
        Assert.Contains("La javanaise", titles);
    }

    [Fact]
    public async Task GetTopSongs_NeverReturnsMoreThanCount()
    {
        _mockMetadataService
            .Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Song>
            {
                External("Initials B.B."),
                External("La javanaise"),
                External("La chanson de Prevert")
            });

        var controller = CreateController(
            new Dictionary<string, string>
            {
                { "artist", "Serge Gainsbourg" },
                { "count", "2" },
                { "f", "json" }
            },
            NavidromeTopSongs(("1", "Je Suis Venu Te Dire Que Je M'en Vais")));

        var titles = TitlesOf(await controller.GetTopSongs());

        Assert.Equal(2, titles.Count);
        Assert.Equal("Je Suis Venu Te Dire Que Je M'en Vais", titles[0]);
    }

    [Fact]
    public async Task GetTopSongs_WithoutArtistParameter_ReturnsSubsonicError10()
    {
        var controller = CreateController(
            new Dictionary<string, string> { { "f", "json" } },
            NavidromeTopSongs());

        var result = await controller.GetTopSongs();

        var json = Assert.IsType<JsonResult>(result);
        using var doc = JsonSerializer.SerializeToDocument(json.Value!);
        var error = doc.RootElement.GetProperty("subsonic-response").GetProperty("error");
        Assert.Equal(10, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task GetTopSongs_WhenProviderFails_StillReturnsTheLocalSongs()
    {
        _mockMetadataService
            .Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        var controller = CreateController(
            new Dictionary<string, string>
            {
                { "artist", "Serge Gainsbourg" },
                { "f", "json" }
            },
            NavidromeTopSongs(("1", "Je Suis Venu Te Dire Que Je M'en Vais")));

        var titles = TitlesOf(await controller.GetTopSongs());

        Assert.Single(titles);
        Assert.Equal("Je Suis Venu Te Dire Que Je M'en Vais", titles[0]);
    }
}
