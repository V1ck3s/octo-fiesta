using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;
using octo_fiesta.Services.Tidal;

namespace octo_fiesta.Tests;

/// <summary>
/// Covers the playback path: the BTS and DASH manifests returned by
/// playbackinfopostpaywall, the quality ladder walked when a tier is refused, and the
/// decryption of the streams Tidal serves encrypted.
/// </summary>
public class TidalDownloadServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _storePath;

    public TidalDownloadServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "octo-fiesta-tidal-download-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDirectory);
        _storePath = Path.Combine(_testDirectory, "tidal-tokens.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private TidalDownloadService CreateService(TidalStubHandler handler, string? quality = null)
    {
        handler.Respond("oauth2/token", TidalTestFactory.TokenResponse);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDirectory
            })
            .Build();

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(PlaylistSyncService))).Returns(null!);

        var tidalSettings = new TidalSettings { RefreshToken = "refresh-token", Quality = quality };

        return new TidalDownloadService(
            TidalTestFactory.HttpClientFactory(handler),
            configuration,
            Mock.Of<ILocalLibraryService>(),
            Mock.Of<IMusicMetadataService>(),
            Options.Create(new SubsonicSettings()),
            Options.Create(tidalSettings),
            TidalTestFactory.AuthService(handler, _storePath, tidalSettings),
            serviceProvider.Object,
            Mock.Of<ILogger<TidalDownloadService>>());
    }

    private static string PlaybackInfo(string manifest, string mimeType, string quality = "LOSSLESS",
        string assetPresentation = "FULL")
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest));
        return $$"""
            {
              "trackId": 12345,
              "audioQuality": "{{quality}}",
              "assetPresentation": "{{assetPresentation}}",
              "manifestMimeType": "{{mimeType}}",
              "manifest": "{{encoded}}"
            }
            """;
    }

    private const string BtsManifest = """
        {
          "mimeType": "audio/flac",
          "codecs": "flac",
          "encryptionType": "NONE",
          "keyId": null,
          "urls": ["https://cdn.example/track.flac"]
        }
        """;

    private const string DashManifest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT3M20.000S">
          <Period>
            <AdaptationSet mimeType="audio/mp4" segmentAlignment="true">
              <Representation id="1" codecs="flac" bandwidth="3200000">
                <SegmentTemplate timescale="44100"
                                 initialization="https://cdn.example/init.mp4"
                                 media="https://cdn.example/chunk-$Number$.mp4"
                                 startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="4410000" r="1"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    /// <summary>
    /// A ~10s manifest. For a 200s track it means the account cannot stream the track in full.
    /// </summary>
    private const string ShortDashManifest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT0M10.000S">
          <Period>
            <AdaptationSet mimeType="audio/mp4" segmentAlignment="true">
              <Representation id="1" codecs="flac" bandwidth="3200000">
                <SegmentTemplate timescale="44100"
                                 initialization="https://cdn.example/init.mp4"
                                 media="https://cdn.example/chunk-$Number$.mp4"
                                 startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="441000"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    #region Manifest handling

    [Fact]
    public async Task GetManifestAsync_BtsManifest_ReturnsTheDirectUrl()
    {
        var handler = new TidalStubHandler()
            .Respond("playbackinfopostpaywall", PlaybackInfo(BtsManifest, "application/vnd.tidal.bts"));

        var (manifest, quality) = await CreateService(handler, "LOSSLESS")
            .GetManifestAsync("12345", "LOSSLESS", 200, CancellationToken.None);

        Assert.Equal("LOSSLESS", quality);
        Assert.Equal("audio/flac", manifest!.MimeType);
        Assert.Equal("https://cdn.example/track.flac", Assert.Single(manifest.Urls!));
    }

    [Fact]
    public async Task GetManifestAsync_DashManifest_FlattensSegmentsWithTheInitFirst()
    {
        var handler = new TidalStubHandler()
            .Respond("playbackinfopostpaywall", PlaybackInfo(DashManifest, "application/dash+xml", "HI_RES_LOSSLESS"));

        var (manifest, quality) = await CreateService(handler)
            .GetManifestAsync("12345", "HI_RES_LOSSLESS", 200, CancellationToken.None);

        Assert.Equal("HI_RES_LOSSLESS", quality);
        Assert.Equal("flac", manifest!.Codecs);
        Assert.Equal(
            ["https://cdn.example/init.mp4", "https://cdn.example/chunk-1.mp4", "https://cdn.example/chunk-2.mp4"],
            manifest.Urls);
        Assert.Equal(200, manifest.DurationSeconds);
    }

    [Fact]
    public async Task GetManifestAsync_SendsTheRequestedQualityAndCountryCode()
    {
        var handler = new TidalStubHandler()
            .Respond("playbackinfopostpaywall", PlaybackInfo(BtsManifest, "application/vnd.tidal.bts"));

        await CreateService(handler).GetManifestAsync("12345", "LOSSLESS", 200, CancellationToken.None);

        var url = handler.Requests.Last().RequestUri!.ToString();
        Assert.Contains("audioquality=LOSSLESS", url);
        Assert.Contains("playbackmode=STREAM", url);
        Assert.Contains("assetpresentation=FULL", url);
        Assert.Contains("countryCode=FR", url);
    }

    #endregion

    #region Quality fallback

    [Fact]
    public async Task GetManifestAsync_WhenTidalServesALowerTier_ReportsWhatItServed()
    {
        // Tidal answers 200 with the tier the account is entitled to rather than refusing,
        // so the file must be labelled from the delivered quality, not the requested one.
        var handler = new TidalStubHandler().Respond("playbackinfopostpaywall",
            PlaybackInfo(BtsManifest, "application/vnd.tidal.bts", "HIGH"));

        var (_, quality) = await CreateService(handler)
            .GetManifestAsync("12345", "HI_RES_LOSSLESS", 200, CancellationToken.None);

        Assert.Equal("HIGH", quality);
    }

    [Fact]
    public async Task GetManifestAsync_WhenATierIsRefused_RetriesOneTierLower()
    {
        var handler = new TidalStubHandler().Respond("playbackinfopostpaywall", request =>
            request.RequestUri!.ToString().Contains("audioquality=HI_RES_LOSSLESS")
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"status":401,"subStatus":4006,"userMessage":"Asset is not ready for playback"}""")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PlaybackInfo(BtsManifest, "application/vnd.tidal.bts"))
                });

        var (manifest, quality) = await CreateService(handler)
            .GetManifestAsync("12345", "HI_RES_LOSSLESS", 200, CancellationToken.None);

        Assert.Equal("LOSSLESS", quality);
        Assert.NotNull(manifest);
        Assert.Equal(2, handler.Requests.Count(r => r.RequestUri!.ToString().Contains("playbackinfo")));
    }

    [Fact]
    public async Task GetManifestAsync_APreviewClip_FallsBackInsteadOfSavingIt()
    {
        var handler = new TidalStubHandler().Respond("playbackinfopostpaywall", request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.ToString().Contains("audioquality=HI_RES_LOSSLESS")
                    ? PlaybackInfo(BtsManifest, "application/vnd.tidal.bts", "HI_RES_LOSSLESS", "PREVIEW")
                    : PlaybackInfo(BtsManifest, "application/vnd.tidal.bts"))
            });

        var (_, quality) = await CreateService(handler)
            .GetManifestAsync("12345", "HI_RES_LOSSLESS", 200, CancellationToken.None);

        Assert.Equal("LOSSLESS", quality);
    }

    [Fact]
    public async Task GetManifestAsync_AtTheLowestTier_Fails()
    {
        var handler = new TidalStubHandler().Respond("playbackinfopostpaywall",
            """{"status":401,"subStatus":4006,"userMessage":"Asset is not ready for playback"}""",
            HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(handler).GetManifestAsync("12345", "LOW", 200, CancellationToken.None));

        Assert.Contains("12345", exception.Message);
    }

    [Fact]
    public async Task GetManifestAsync_AShortManifestForALongTrack_IsTreatedAsAPreview()
    {
        var handler = new TidalStubHandler().Respond("playbackinfopostpaywall", request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.ToString().Contains("audioquality=HI_RES_LOSSLESS")
                    ? PlaybackInfo(ShortDashManifest, "application/dash+xml", "HI_RES_LOSSLESS")
                    : PlaybackInfo(BtsManifest, "application/vnd.tidal.bts"))
            });

        var (_, quality) = await CreateService(handler)
            .GetManifestAsync("12345", "HI_RES_LOSSLESS", 200, CancellationToken.None);

        Assert.Equal("LOSSLESS", quality);
    }

    #endregion

    #region Stream decryption

    [Fact]
    public void DecryptSecurityToken_UnwrapsTheKeyAndNonce()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(8);

        var (decryptedKey, decryptedNonce) = TidalStreamDecryptor.DecryptSecurityToken(BuildSecurityToken(key, nonce));

        Assert.Equal(key, decryptedKey);
        Assert.Equal(nonce, decryptedNonce);
    }

    [Fact]
    public async Task Decrypt_ReadsAnEncryptedStreamBackInClear()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(8);
        var plaintext = RandomNumberGenerator.GetBytes(4096);

        var counter = new byte[16];
        nonce.CopyTo(counter, 0);
        var encrypted = AesCtr(plaintext, key, counter);

        await using var decrypted = TidalStreamDecryptor.Decrypt(
            new MemoryStream(encrypted), BuildSecurityToken(key, nonce));

        using var buffer = new MemoryStream();
        await decrypted.CopyToAsync(buffer);

        Assert.Equal(plaintext, buffer.ToArray());
    }

    [Fact]
    public void DecryptSecurityToken_TooShortToken_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            TidalStreamDecryptor.DecryptSecurityToken(Convert.ToBase64String(new byte[16])));
    }

    /// <summary>
    /// Wraps a key and nonce the way Tidal does: AES-CBC under the master key, IV first.
    /// </summary>
    private static string BuildSecurityToken(byte[] key, byte[] nonce)
    {
        var payload = new byte[32];
        key.CopyTo(payload, 0);
        nonce.CopyTo(payload, 16);

        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = Convert.FromBase64String("UIlTTEMmmLfGowo/UC60x2H45W6MdGgTRfo/umg4754=");
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        var encrypted = aes.CreateEncryptor().TransformFinalBlock(payload, 0, payload.Length);
        return Convert.ToBase64String([.. iv, .. encrypted]);
    }

    private static byte[] AesCtr(byte[] data, byte[] key, byte[] counter)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        var encryptor = aes.CreateEncryptor();

        var block = (byte[])counter.Clone();
        var result = new byte[data.Length];

        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var keystream = encryptor.TransformFinalBlock(block, 0, 16);
            for (var i = 0; i < 16 && offset + i < data.Length; i++)
            {
                result[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            }

            // 64-bit big-endian counter in the low half of the block.
            for (var i = 15; i >= 8; i--)
            {
                if (++block[i] != 0) break;
            }
        }

        return result;
    }

    #endregion
}
