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
    [InlineData(null, "AAC_320", true)]
    [InlineData(null, "AAC_96", true)]
    // AAC_96 (lowest) to higher quality
    [InlineData("AAC_96", "MP3_128", true)]
    [InlineData("AAC_96", "AAC_320", true)]
    [InlineData("AAC_96", "MP3_320", true)]
    [InlineData("AAC_96", "FLAC", true)]
    [InlineData("AAC_96", "FLAC_24", true)]
    // MP3_128 to higher quality
    [InlineData("MP3_128", "MP3_320", true)]
    [InlineData("MP3_128", "AAC_320", true)]
    [InlineData("MP3_128", "FLAC", true)]
    [InlineData("MP3_128", "FLAC_16", true)]
    [InlineData("MP3_128", "FLAC_24_LOW", true)]
    [InlineData("MP3_128", "FLAC_24_HIGH", true)]
    // AAC_320/MP3_320 to higher quality (same level, both at 2)
    [InlineData("AAC_320", "FLAC", true)]
    [InlineData("AAC_320", "FLAC_16", true)]
    [InlineData("AAC_320", "FLAC_24", true)]
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
    [InlineData("FLAC_24", "FLAC_24_HIGH", true)]
    // Same or lower quality should not upgrade
    [InlineData("AAC_96", "AAC_96", false)]
    [InlineData("AAC_320", "AAC_96", false)]
    [InlineData("AAC_320", "MP3_128", false)]
    [InlineData("AAC_320", "MP3_320", false)]  // Same level
    [InlineData("MP3_320", "AAC_320", false)]  // Same level
    [InlineData("FLAC", "FLAC", false)]
    [InlineData("FLAC", "FLAC_16", false)]
    [InlineData("FLAC_16", "FLAC", false)]
    [InlineData("MP3_320", "MP3_320", false)]
    [InlineData("MP3_320", "MP3_128", false)]
    [InlineData("FLAC", "MP3_320", false)]
    [InlineData("FLAC", "AAC_320", false)]
    [InlineData("FLAC_24_HIGH", "FLAC_24_LOW", false)]
    [InlineData("FLAC_24_HIGH", "FLAC_24", false)]
    [InlineData("FLAC_24_HIGH", "FLAC", false)]
    [InlineData("FLAC_24_HIGH", "MP3_320", false)]
    [InlineData("FLAC_24_HIGH", "AAC_320", false)]
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
    [InlineData("AAC_96", 0)]
    [InlineData("MP3_128", 1)]
    [InlineData("AAC_320", 2)]
    [InlineData("MP3_320", 2)]
    [InlineData("FLAC", 3)]
    [InlineData("FLAC_16", 3)]
    [InlineData("FLAC_24", 4)]
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
