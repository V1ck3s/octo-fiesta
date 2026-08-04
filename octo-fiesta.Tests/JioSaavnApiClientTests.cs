using System.Security.Cryptography;
using System.Text;
using octo_fiesta.Services.JioSaavn;

namespace octo_fiesta.Tests;

/// <summary>
/// Pure logic in JioSaavnApiClient: perma-URL id encoding, DES media-URL decryption, and
/// quality-suffix substitution. No HTTP involved - deterministic and directly exercises the
/// crypto/parsing that the download flow depends on.
/// </summary>
public class JioSaavnApiClientTests
{
    private static readonly byte[] DesKey = Encoding.ASCII.GetBytes("38346591");

    private static string EncryptForTest(string plaintext)
    {
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        des.Key = DesKey;
        using var encryptor = des.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
    }

    // ---- Perma-URL external ID encode/decode ----

    [Theory]
    [InlineData("https://www.jiosaavn.com/song/title/abc123")]
    [InlineData("https://www.jiosaavn.com/song/%d0%9a%d1%80%d0%b0%d1%81%d0%b8%d0%b2%d0%be%d0%b5/QToYZ01jfVc")]
    public void EncodeDecodePermaUrlExternalId_RoundTrips(string permaUrl)
    {
        var encoded = JioSaavnApiClient.EncodePermaUrlExternalId(permaUrl);
        var decoded = JioSaavnApiClient.DecodePermaUrlExternalId(encoded);
        Assert.Equal(permaUrl, decoded);
    }

    [Fact]
    public void EncodePermaUrlExternalId_ProducesUrlSafeCharacters()
    {
        var encoded = JioSaavnApiClient.EncodePermaUrlExternalId("https://www.jiosaavn.com/song/x/y?a=b&c=d");
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Fact]
    public void EncodePermaUrlExternalId_WithEmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => JioSaavnApiClient.EncodePermaUrlExternalId(""));
    }

    [Fact]
    public void DecodePermaUrlExternalId_WithEmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => JioSaavnApiClient.DecodePermaUrlExternalId(""));
    }

    // ---- Media URL decryption ----

    [Fact]
    public void DecryptMediaUrl_WithValidCiphertext_ReturnsOriginalPlaintext()
    {
        const string plaintext = "https://aac.saavncdn.com/259/song_160.mp4";
        var encrypted = EncryptForTest(plaintext);

        var decrypted = JioSaavnApiClient.DecryptMediaUrl(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DecryptMediaUrl_AcceptsUrlSafeBase64Variant()
    {
        const string plaintext = "https://aac.saavncdn.com/259/song_320.mp4";
        var standardBase64 = EncryptForTest(plaintext);
        var urlSafe = standardBase64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var decrypted = JioSaavnApiClient.DecryptMediaUrl(urlSafe);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DecryptMediaUrl_WithEmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => JioSaavnApiClient.DecryptMediaUrl(""));
    }

    // ---- Quality suffix substitution ----

    [Theory]
    [InlineData("https://aac.saavncdn.com/259/song_96.mp4", 320, "https://aac.saavncdn.com/259/song_320.mp4")]
    [InlineData("https://aac.saavncdn.com/259/song_320.mp4", 96, "https://aac.saavncdn.com/259/song_96.mp4")]
    [InlineData("https://aac.saavncdn.com/259/song_160.mp4?t=abc", 320, "https://aac.saavncdn.com/259/song_320.mp4?t=abc")]
    public void GetQualityUrl_ReplacesQualitySuffix(string input, int kbps, string expected)
    {
        var result = JioSaavnApiClient.GetQualityUrl(input, kbps);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetQualityUrl_WithoutReplaceableSuffix_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            JioSaavnApiClient.GetQualityUrl("https://aac.saavncdn.com/259/song.mp4", 320));
    }

    // ---- Quality normalization ----

    [Theory]
    [InlineData(1, 96)]
    [InlineData(96, 96)]
    [InlineData(97, 160)]
    [InlineData(160, 160)]
    [InlineData(161, 320)]
    [InlineData(320, 320)]
    [InlineData(999, 320)]
    public void NormalizeQuality_SnapsToNearestSupportedTier(int input, int expected)
    {
        Assert.Equal(expected, JioSaavnApiClient.NormalizeQuality(input));
    }
}
