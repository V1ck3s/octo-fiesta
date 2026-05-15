using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.SquidWTF;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

/// <summary>
/// Covers the Tidal HI_RES_LOSSLESS DASH download path:
///   1. <see cref="TidalDashManifestParser"/> — XML → ordered URL list (init + segments).
///   2. <see cref="SquidWTFDownloadService.GetTidalManifestAsync"/> — base64-decodes the
///      manifest from /track/ and wires DASH segments into a <see cref="Models.SquidWTF.TidalManifest"/>.
/// </summary>
public class SquidWTFTidalDashTests : IDisposable
{
    private readonly string _testDownloadPath;

    public SquidWTFTidalDashTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-squid-tidal-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    // --- TidalDashManifestParser ----------------------------------------------------------

    // Tidal HI_RES_LOSSLESS shape: SegmentTemplate + SegmentTimeline + absolute URLs + FLAC codec.
    private const string TidalHiResDashXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"
             type="static"
             mediaPresentationDuration="PT0M10.500S">
          <Period>
            <AdaptationSet mimeType="audio/mp4" segmentAlignment="true">
              <Representation id="1" codecs="flac" bandwidth="3200000">
                <SegmentTemplate timescale="44100"
                                 initialization="https://cdn.example/track/init.mp4"
                                 media="https://cdn.example/track/chunk-$Number$.mp4"
                                 startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="220500" r="1"/>
                    <S d="22050"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void Parser_TidalHiResDash_ReturnsInitPlusOrderedSegmentUrls()
    {
        var parsed = TidalDashManifestParser.Parse(TidalHiResDashXml);

        // r="1" → first <S> repeats once (2 segments), second <S> adds one, plus init = 4.
        Assert.Equal(new[]
        {
            "https://cdn.example/track/init.mp4",
            "https://cdn.example/track/chunk-1.mp4",
            "https://cdn.example/track/chunk-2.mp4",
            "https://cdn.example/track/chunk-3.mp4",
        }, parsed.Urls);
        Assert.Equal("flac", parsed.Codecs);
        Assert.Equal("audio/mp4", parsed.MimeType);
    }

    [Fact]
    public void Parser_HandlesSegmentList()
    {
        var xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet mimeType="audio/mp4">
                  <Representation id="1" codecs="flac">
                    <SegmentList>
                      <Initialization sourceURL="https://cdn.example/init.mp4"/>
                      <SegmentURL media="https://cdn.example/seg-1.mp4"/>
                      <SegmentURL media="https://cdn.example/seg-2.mp4"/>
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var parsed = TidalDashManifestParser.Parse(xml);

        Assert.Equal(new[]
        {
            "https://cdn.example/init.mp4",
            "https://cdn.example/seg-1.mp4",
            "https://cdn.example/seg-2.mp4",
        }, parsed.Urls);
    }

    [Fact]
    public void Parser_HandlesFixedDurationTemplate()
    {
        // 10.5s total / 5s per segment, ceil → 3 segments + init
        var xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"
                 type="static"
                 mediaPresentationDuration="PT10.5S">
              <Period>
                <AdaptationSet mimeType="audio/mp4">
                  <Representation id="1" codecs="flac">
                    <SegmentTemplate timescale="1"
                                     duration="5"
                                     initialization="https://cdn.example/init.mp4"
                                     media="https://cdn.example/seg-$Number$.mp4"
                                     startNumber="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var parsed = TidalDashManifestParser.Parse(xml);

        Assert.Equal(new[]
        {
            "https://cdn.example/init.mp4",
            "https://cdn.example/seg-1.mp4",
            "https://cdn.example/seg-2.mp4",
            "https://cdn.example/seg-3.mp4",
        }, parsed.Urls);
    }

    [Fact]
    public void Parser_ThrowsWhenNoSegmentInfo()
    {
        var xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet>
                  <Representation id="1" codecs="flac"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        Assert.Throws<InvalidOperationException>(() => TidalDashManifestParser.Parse(xml));
    }

    // --- GetTidalManifestAsync end-to-end -------------------------------------------------

    [Fact]
    public async Task GetTidalManifestAsync_DashManifest_ReturnsFlacSegmentUrls()
    {
        var dashB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(TidalHiResDashXml));
        var trackJson = $$"""
            {
              "version": "1.0",
              "data": {
                "trackId": 414221994,
                "audioQuality": "HI_RES_LOSSLESS",
                "manifest": "{{dashB64}}",
                "manifestMimeType": "application/dash+xml"
              }
            }
            """;

        var (service, _) = BuildService(req =>
        {
            Assert.Contains("/track/", req.RequestUri!.AbsolutePath);
            Assert.Contains("quality=HI_RES_LOSSLESS", req.RequestUri.Query);
            return BuildResponse(HttpStatusCode.OK, trackJson);
        });

        var (manifest, quality) = await service.GetTidalManifestAsync("414221994", "HI_RES_LOSSLESS", CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("HI_RES_LOSSLESS", quality);
        Assert.Equal("audio/mp4", manifest!.MimeType);
        Assert.Equal("flac", manifest.Codecs);
        Assert.Equal(new[]
        {
            "https://cdn.example/track/init.mp4",
            "https://cdn.example/track/chunk-1.mp4",
            "https://cdn.example/track/chunk-2.mp4",
            "https://cdn.example/track/chunk-3.mp4",
        }, manifest.Urls);
    }

    [Fact]
    public async Task GetTidalManifestAsync_LegacyBtsJsonManifest_StillWorks()
    {
        var btsJson = """{"mimeType":"audio/flac","codecs":"flac","urls":["https://cdn.example/track.flac"]}""";
        var btsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(btsJson));
        var trackJson = $$"""
            {
              "data": {
                "trackId": 1,
                "audioQuality": "LOSSLESS",
                "manifest": "{{btsB64}}",
                "manifestMimeType": "application/vnd.tidal.bts"
              }
            }
            """;

        var (service, _) = BuildService(_ => BuildResponse(HttpStatusCode.OK, trackJson));

        var (manifest, quality) = await service.GetTidalManifestAsync("1", "LOSSLESS", CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("LOSSLESS", quality);
        Assert.Single(manifest!.Urls!);
        Assert.Equal("https://cdn.example/track.flac", manifest.Urls![0]);
    }

    [Fact]
    public async Task GetTidalManifestAsync_HiResDashWithParseError_FallsBackToLossless()
    {
        var brokenXml = "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\"><Period/></MPD>";
        var brokenB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(brokenXml));

        var losslessJson = """{"mimeType":"audio/flac","codecs":"flac","urls":["https://cdn.example/lossless.flac"]}""";
        var losslessB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(losslessJson));

        var brokenTrackJson = "{\"data\":{\"manifest\":\"" + brokenB64 + "\",\"manifestMimeType\":\"application/dash+xml\"}}";
        var losslessTrackJson = "{\"data\":{\"manifest\":\"" + losslessB64 + "\",\"manifestMimeType\":\"application/vnd.tidal.bts\"}}";

        var responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            req =>
            {
                Assert.Contains("quality=HI_RES_LOSSLESS", req.RequestUri!.Query);
                return BuildResponse(HttpStatusCode.OK, brokenTrackJson);
            },
            req =>
            {
                Assert.Contains("quality=LOSSLESS", req.RequestUri!.Query);
                return BuildResponse(HttpStatusCode.OK, losslessTrackJson);
            },
        });

        var (service, _) = BuildService(req => responses.Dequeue()(req));

        var (manifest, quality) = await service.GetTidalManifestAsync("1", "HI_RES_LOSSLESS", CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("LOSSLESS", quality);
        Assert.Equal("https://cdn.example/lossless.flac", manifest!.Urls![0]);
    }

    // --- helpers --------------------------------------------------------------------------

    private static HttpResponseMessage BuildResponse(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private (SquidWTFDownloadService Service, Mock<HttpMessageHandler> Handler) BuildService(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) => Task.FromResult(respond(req)));

        var httpClient = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var squidSettings = Options.Create(new SquidWTFSettings
        {
            Source = "Tidal",
            Quality = "HI_RES_LOSSLESS",
            Instances = new List<string> { "https://test.local" },
            InstanceTimeoutSeconds = 30,
        });

        var instanceManager = new SquidWTFInstanceManager(
            factory.Object, squidSettings,
            new Mock<ILogger<SquidWTFInstanceManager>>().Object);

        var captchaSolver = new SquidWTFCaptchaSolver(
            factory.Object,
            new Mock<ILogger<SquidWTFCaptchaSolver>>().Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath,
            })
            .Build();

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(PlaylistSyncService))).Returns(null!);

        var service = new SquidWTFDownloadService(
            factory.Object,
            configuration,
            new Mock<ILocalLibraryService>().Object,
            new Mock<IMusicMetadataService>().Object,
            Options.Create(new SubsonicSettings()),
            squidSettings,
            instanceManager,
            captchaSolver,
            serviceProviderMock.Object,
            new Mock<ILogger<SquidWTFDownloadService>>().Object);

        return (service, handler);
    }
}
