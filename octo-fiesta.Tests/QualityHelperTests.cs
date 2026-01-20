using octo_fiesta.Services.Common;
using Xunit;

namespace octo_fiesta.Tests;

public class QualityHelperTests
{
    [Theory]
    // Legacy downloads (null quality) should always upgrade
    [InlineData(null, "FLAC", true)]
    [InlineData(null, "MP3_320", true)]
    [InlineData(null, "MP3_128", true)]
    // MP3_128 to higher quality
    [InlineData("MP3_128", "MP3_320", true)]
    [InlineData("MP3_128", "FLAC", true)]
    [InlineData("MP3_128", "FLAC_16", true)]
    [InlineData("MP3_128", "FLAC_24_LOW", true)]
    [InlineData("MP3_128", "FLAC_24_HIGH", true)]
    // MP3_320 to higher quality
    [InlineData("MP3_320", "FLAC", true)]
    [InlineData("MP3_320", "FLAC_16", true)]
    [InlineData("MP3_320", "FLAC_24_LOW", true)]
    [InlineData("MP3_320", "FLAC_24_HIGH", true)]
    // FLAC to higher quality
    [InlineData("FLAC", "FLAC_24_LOW", true)]
    [InlineData("FLAC", "FLAC_24_HIGH", true)]
    [InlineData("FLAC_16", "FLAC_24_LOW", true)]
    [InlineData("FLAC_16", "FLAC_24_HIGH", true)]
    [InlineData("FLAC_24_LOW", "FLAC_24_HIGH", true)]
    // Same or lower quality should not upgrade
    [InlineData("FLAC", "FLAC", false)]
    [InlineData("FLAC", "FLAC_16", false)]
    [InlineData("FLAC_16", "FLAC", false)]
    [InlineData("MP3_320", "MP3_320", false)]
    [InlineData("MP3_320", "MP3_128", false)]
    [InlineData("FLAC", "MP3_320", false)]
    [InlineData("FLAC_24_HIGH", "FLAC_24_LOW", false)]
    [InlineData("FLAC_24_HIGH", "FLAC", false)]
    [InlineData("FLAC_24_HIGH", "MP3_320", false)]
    public void ShouldUpgrade_ReturnsExpectedResult(string? existingQuality, string targetQuality, bool expected)
    {
        var result = QualityHelper.ShouldUpgrade(existingQuality, targetQuality);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("MP3_128", null)]
    [InlineData("MP3_128", "")]
    [InlineData("FLAC", null)]
    [InlineData("FLAC", "")]
    public void ShouldUpgrade_WithNullOrEmptyTarget_ReturnsFalse(string? existingQuality, string? targetQuality)
    {
        var result = QualityHelper.ShouldUpgrade(existingQuality, targetQuality);
        Assert.False(result);
    }

    [Theory]
    [InlineData("MP3_128", 1)]
    [InlineData("MP3_320", 2)]
    [InlineData("FLAC", 3)]
    [InlineData("FLAC_16", 3)]
    [InlineData("FLAC_24_LOW", 4)]
    [InlineData("FLAC_24_HIGH", 5)]
    [InlineData("unknown", 0)]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    public void GetQualityLevel_ReturnsExpectedLevel(string? quality, int expectedLevel)
    {
        var result = QualityHelper.GetQualityLevel(quality);
        Assert.Equal(expectedLevel, result);
    }
}
