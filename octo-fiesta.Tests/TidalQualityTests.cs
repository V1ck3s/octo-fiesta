using octo_fiesta.Services.Tidal;

namespace octo_fiesta.Tests;

public class TidalQualityTests
{
    [Theory]
    [InlineData(null, "HI_RES_LOSSLESS")]
    [InlineData("", "HI_RES_LOSSLESS")]
    [InlineData("lossless", "LOSSLESS")]
    [InlineData("FLAC", "LOSSLESS")]
    [InlineData("FLAC_16", "LOSSLESS")]
    [InlineData("FLAC_24", "HI_RES_LOSSLESS")]
    [InlineData("  HIGH  ", "HIGH")]
    [InlineData("AAC_96", "LOW")]
    [InlineData("nonsense", "HI_RES_LOSSLESS")]
    public void Normalize_MapsConfiguredNamesToApiCodes(string? configured, string expected)
    {
        Assert.Equal(expected, TidalQuality.Normalize(configured));
    }

    [Fact]
    public void Normalize_KeepsLegacyHiResTier()
    {
        // HI_RES is the legacy MQA tier and stays distinct from HI_RES_LOSSLESS.
        Assert.Equal("HI_RES", TidalQuality.Normalize("HI_RES"));
    }

    [Theory]
    [InlineData("HI_RES_LOSSLESS", "LOSSLESS")]
    [InlineData("LOSSLESS", "HIGH")]
    [InlineData("HIGH", "LOW")]
    [InlineData("HI_RES", "LOSSLESS")]
    public void NextLower_WalksDownTheLadder(string quality, string expected)
    {
        Assert.Equal(expected, TidalQuality.NextLower(quality));
    }

    [Fact]
    public void NextLower_AtLowestTier_ReturnsNull()
    {
        Assert.Null(TidalQuality.NextLower("LOW"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LOSSLESS")]
    [InlineData("hi_res_lossless")]
    [InlineData("HI_RES")]
    public void IsValid_AcceptsSupportedAndEmptyValues(string? quality)
    {
        Assert.True(TidalQuality.IsValid(quality));
    }

    [Fact]
    public void IsValid_RejectsUnknownValue()
    {
        Assert.False(TidalQuality.IsValid("FLAC_192"));
    }

    [Fact]
    public void GetExtension_FlacInMp4_KeepsTheMp4Container()
    {
        // HI_RES_LOSSLESS DASH delivers FLAC inside fragmented MP4: lossless audio, MP4 bytes.
        Assert.Equal(".m4a", TidalQuality.GetExtension("audio/mp4", "flac"));
    }

    [Theory]
    [InlineData("audio/flac", "flac", ".flac")]
    [InlineData("audio/mp4", "mp4a.40.2", ".m4a")]
    [InlineData("audio/mpeg", null, ".mp3")]
    public void GetExtension_MapsMimeTypes(string mimeType, string? codecs, string expected)
    {
        Assert.Equal(expected, TidalQuality.GetExtension(mimeType, codecs));
    }

    [Theory]
    [InlineData("HI_RES_LOSSLESS", "audio/mp4", "flac", "FLAC_24")]
    [InlineData("LOSSLESS", "audio/flac", "flac", "FLAC_16")]
    [InlineData("HIGH", "audio/mp4", "mp4a.40.2", "AAC_320")]
    [InlineData("LOW", "audio/mp4", "mp4a.40.5", "AAC_96")]
    public void GetDownloadedQuality_ReportsTheDeliveredTier(
        string requested, string mimeType, string codecs, string expected)
    {
        Assert.Equal(expected, TidalQuality.GetDownloadedQuality(requested, mimeType, codecs));
    }
}
