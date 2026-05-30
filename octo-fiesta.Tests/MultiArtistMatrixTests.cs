using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Qobuz;
using octo_fiesta.Services.SquidWTF;
using octo_fiesta.Services.Subsonic;
using octo_fiesta.Services.Yandex;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Xml.Linq;

namespace octo_fiesta.Tests;

/// <summary>
/// Matrix coverage for the multi-artist fix (#222).
///
/// Two dimensions:
///   1. Each provider must populate <see cref="Song.Artists"/> with identified
///      artists (id + name), exposing multiple where the source has them.
///   2. The Subsonic serializer must emit those artists (OpenSubsonic `artists`
///      array in JSON, repeated &lt;artists&gt; elements in XML) so clients display them.
///
/// The git history of this file shows the pre-fix form, where every test instead
/// asserted the broken state (no `artists` output) and passed on the unfixed code.
/// </summary>
public class MultiArtistMatrixTests
{
    private readonly SubsonicResponseBuilder _builder = new();
    private static readonly XNamespace Ns = "http://subsonic.org/restapi";

    // ---- infrastructure helpers ----

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var f = new Mock<IHttpClientFactory>();
        f.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("https://example.org") });
        return f.Object;
    }

    /// <summary>A handler that returns the same JSON body (fresh instance) for every request.</summary>
    private static HttpMessageHandler JsonHandler(string json)
    {
        var h = new Mock<HttpMessageHandler>();
        h.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            });
        return h.Object;
    }

    private List<(string id, string name)> JsonArtists(Song song)
    {
        var json = _builder.ConvertSongToJson(song);
        Assert.True(json.ContainsKey("artists"), "Subsonic JSON is missing the `artists` array");
        var list = Assert.IsType<List<Dictionary<string, object>>>(json["artists"]);
        return list.Select(d => ((string)d["id"], (string)d["name"])).ToList();
    }

    private List<(string id, string name)> XmlArtists(Song song)
    {
        var el = _builder.ConvertSongToXml(song, Ns);
        return el.Elements(Ns + "artists")
            .Select(e => (e.Attribute("id")!.Value, e.Attribute("name")!.Value))
            .ToList();
    }

    // ---- per-provider service builders ----

    private static DeezerMetadataService Deezer(string json) =>
        new(Factory(JsonHandler(json)), Options.Create(new SubsonicSettings()));

    private static QobuzMetadataService Qobuz(string json)
    {
        var factory = Factory(JsonHandler(json));
        var bundleLogger = Mock.Of<ILogger<QobuzBundleService>>();
        var bundle = new Mock<QobuzBundleService>(factory, bundleLogger) { CallBase = false };
        bundle.Setup(b => b.GetAppIdAsync()).ReturnsAsync("fake-app-id");
        bundle.Setup(b => b.GetSecretsAsync()).ReturnsAsync(new List<string> { "fake-secret" });
        bundle.Setup(b => b.GetSecretAsync(It.IsAny<int>())).ReturnsAsync("fake-secret");
        return new QobuzMetadataService(
            factory,
            Options.Create(new SubsonicSettings()),
            Options.Create(new QobuzSettings { UserAuthToken = "tok", UserId = "1" }),
            bundle.Object,
            Mock.Of<ILogger<QobuzMetadataService>>());
    }

    private static YandexMetadataService Yandex(string json)
    {
        var factory = Factory(JsonHandler(json));
        return new YandexMetadataService(
            factory,
            Options.Create(new YandexSettings { OAuthToken = "tok", Quality = "AAC_256", Language = "ru" }),
            Mock.Of<ILogger<YandexMetadataService>>());
    }

    private static SquidWTFMetadataService SquidWtfTidal(string json)
    {
        var factory = Factory(JsonHandler(json));
        var settings = Options.Create(new SquidWTFSettings
        {
            Source = "Tidal",
            Instances = new List<string> { "https://inst1.test" }
        });
        var instanceManager = new SquidWTFInstanceManager(
            factory, settings, Mock.Of<ILogger<SquidWTFInstanceManager>>());
        return new SquidWTFMetadataService(
            factory,
            settings,
            Options.Create(new SubsonicSettings()),
            instanceManager,
            Mock.Of<ILogger<SquidWTFMetadataService>>());
    }

    // ---- fixtures (multi-artist payloads) ----

    private const string DeezerTrackJson = """
    {
      "id": 123,
      "title": "Deezer Multi",
      "duration": 210,
      "artist": { "id": 7, "name": "Deezer Main" },
      "album": { "id": 70, "title": "Deezer Album", "artist": { "id": 7, "name": "Deezer Main" } },
      "contributors": [
        { "id": 7, "name": "Deezer Main", "role": "Main" },
        { "id": 8, "name": "Deezer Feat", "role": "Featured" }
      ]
    }
    """;

    private const string QobuzTrackJson = """
    {
      "id": 123456789,
      "title": "Take Five",
      "duration": 324,
      "track_number": 1,
      "performer": { "id": 111, "name": "Dave Brubeck Quartet" },
      "album": { "id": 222, "title": "Time Out", "artist": { "id": 111, "name": "Dave Brubeck Quartet" } }
    }
    """;

    private const string YandexTrackJson = """
    {
      "result": [
        {
          "id": "88633197",
          "title": "ABC",
          "durationMs": 224630,
          "artists": [
            { "id": 8624246, "name": "Otica" },
            { "id": 12756620, "name": "David Diesel" },
            { "id": 1062258, "name": "Halogen" }
          ],
          "albums": [
            {
              "id": 17743572,
              "title": "ATL",
              "year": 2021,
              "releaseDate": "2021-09-10T00:00:00+03:00",
              "trackCount": 1,
              "artists": [ { "id": 8624246, "name": "Otica" } ],
              "trackPosition": { "volume": 1, "index": 1 }
            }
          ],
          "coverUri": "get-image.example.org/path/%%%%"
        }
      ]
    }
    """;

    private const string TidalTrackJson = """
    {
      "version": "1",
      "data": {
        "id": 555,
        "title": "Tidal Multi",
        "duration": 200,
        "trackNumber": 1,
        "volumeNumber": 1,
        "explicit": false,
        "artist": { "id": 10, "name": "Tidal Main" },
        "artists": [
          { "id": 10, "name": "Tidal Main" },
          { "id": 11, "name": "Tidal Feat" }
        ],
        "album": { "id": 99, "title": "Tidal Album", "releaseDate": "2020-01-01" }
      }
    }
    """;

    private static Task<Song?> DeezerSong() => Deezer(DeezerTrackJson).GetSongAsync("deezer", "123");
    private static Task<Song?> QobuzSong() => Qobuz(QobuzTrackJson).GetSongAsync("qobuz", "123456789");
    private static Task<Song?> YandexSong() => Yandex(YandexTrackJson).GetSongAsync("yandex", "88633197:17743572");
    private static Task<Song?> TidalSong() => SquidWtfTidal(TidalTrackJson).GetSongAsync("squidwtf", "555");

    // =====================================================================
    // Provider dimension: Song.Artists is populated with id + name
    // =====================================================================

    [Fact]
    public async Task Deezer_PopulatesMultipleIdentifiedArtists()
    {
        var song = await DeezerSong();
        Assert.NotNull(song);
        Assert.Equal(
            new[] { ("ext-deezer-artist-7", "Deezer Main"), ("ext-deezer-artist-8", "Deezer Feat") },
            song!.Artists.Select(a => (a.Id, a.Name)));
    }

    [Fact]
    public async Task Qobuz_PopulatesIdentifiedArtist()
    {
        var song = await QobuzSong();
        Assert.NotNull(song);
        Assert.Equal(
            new[] { ("ext-qobuz-artist-111", "Dave Brubeck Quartet") },
            song!.Artists.Select(a => (a.Id, a.Name)));
    }

    [Fact]
    public async Task Yandex_PopulatesMultipleIdentifiedArtists()
    {
        var song = await YandexSong();
        Assert.NotNull(song);
        Assert.Equal(
            new[]
            {
                ("ext-yandex-artist-8624246", "Otica"),
                ("ext-yandex-artist-12756620", "David Diesel"),
                ("ext-yandex-artist-1062258", "Halogen"),
            },
            song!.Artists.Select(a => (a.Id, a.Name)));
    }

    [Fact]
    public async Task Tidal_PopulatesMultipleIdentifiedArtists()
    {
        var song = await TidalSong();
        Assert.NotNull(song);
        Assert.Equal(
            new[] { ("ext-squidwtf-artist-10", "Tidal Main"), ("ext-squidwtf-artist-11", "Tidal Feat") },
            song!.Artists.Select(a => (a.Id, a.Name)));
    }

    // =====================================================================
    // Serializer dimension: artists reach the client (JSON + XML), per provider
    // =====================================================================

    [Fact]
    public async Task Deezer_Subsonic_EmitsArtists()
    {
        var song = (await DeezerSong())!;
        var expected = new[] { ("ext-deezer-artist-7", "Deezer Main"), ("ext-deezer-artist-8", "Deezer Feat") };
        Assert.Equal(expected, JsonArtists(song));
        Assert.Equal(expected, XmlArtists(song));
    }

    [Fact]
    public async Task Qobuz_Subsonic_EmitsArtists()
    {
        var song = (await QobuzSong())!;
        var expected = new[] { ("ext-qobuz-artist-111", "Dave Brubeck Quartet") };
        Assert.Equal(expected, JsonArtists(song));
        Assert.Equal(expected, XmlArtists(song));
    }

    [Fact]
    public async Task Yandex_Subsonic_EmitsArtists()
    {
        var song = (await YandexSong())!;
        var expected = new[]
        {
            ("ext-yandex-artist-8624246", "Otica"),
            ("ext-yandex-artist-12756620", "David Diesel"),
            ("ext-yandex-artist-1062258", "Halogen"),
        };
        Assert.Equal(expected, JsonArtists(song));
        Assert.Equal(expected, XmlArtists(song));
    }

    [Fact]
    public async Task Tidal_Subsonic_EmitsArtists()
    {
        var song = (await TidalSong())!;
        var expected = new[] { ("ext-squidwtf-artist-10", "Tidal Main"), ("ext-squidwtf-artist-11", "Tidal Feat") };
        Assert.Equal(expected, JsonArtists(song));
        Assert.Equal(expected, XmlArtists(song));
    }

    // =====================================================================
    // Serializer edge cases
    // =====================================================================

    [Fact]
    public void Subsonic_EmptyArtists_ProducesEmptyArtistsCollection()
    {
        var song = new Song { Title = "T", Artist = "Main", ExternalProvider = "deezer", ExternalId = "1" };
        Assert.Empty(JsonArtists(song));
        Assert.Empty(XmlArtists(song));
    }

    [Fact]
    public void Subsonic_HandBuiltMultiArtist_RoundTripsIdAndName()
    {
        var song = new Song
        {
            Title = "T",
            Artist = "A",
            ExternalProvider = "deezer",
            ExternalId = "1",
            Artists = new List<Artist>
            {
                new() { Id = "ext-deezer-artist-1", Name = "A" },
                new() { Id = "ext-deezer-artist-2", Name = "B" },
            }
        };
        var expected = new[] { ("ext-deezer-artist-1", "A"), ("ext-deezer-artist-2", "B") };
        Assert.Equal(expected, JsonArtists(song));
        Assert.Equal(expected, XmlArtists(song));
    }
}
