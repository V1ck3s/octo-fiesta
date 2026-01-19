using octo_fiesta.Services.Common;
using Xunit;

namespace octo_fiesta.Tests;

public class QualityHelperTests
{
    [Theory]
    [InlineData("/music/artist/album/track.mp3", "FLAC", true)]
    [InlineData("/music/artist/album/track.mp3", "flac", true)]
    [InlineData("/music/artist/album/track.MP3", "FLAC", true)]
    [InlineData("/music/artist/album/track.flac", "FLAC", false)]
    [InlineData("/music/artist/album/track.FLAC", "FLAC", false)]
    [InlineData("/music/artist/album/track.mp3", "MP3_320", false)]
    [InlineData("/music/artist/album/track.mp3", "MP3_128", false)]
    [InlineData("/music/artist/album/track.flac", "MP3_320", false)]
    public void ShouldUpgrade_ReturnsExpectedResult(string existingPath, string targetQuality, bool expected)
    {
        var result = QualityHelper.ShouldUpgrade(existingPath, targetQuality);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/music/track.mp3", null)]
    [InlineData("/music/track.mp3", "")]
    public void ShouldUpgrade_WithNullOrEmptyTarget_ReturnsFalse(string existingPath, string? targetQuality)
    {
        var result = QualityHelper.ShouldUpgrade(existingPath, targetQuality);
        Assert.False(result);
    }

    [Theory]
    [InlineData("/music/track.mp3", "FLAC_24_HIGH", true)]
    [InlineData("/music/track.mp3", "FLAC_24_LOW", true)]
    [InlineData("/music/track.mp3", "FLAC_16", true)]
    public void ShouldUpgrade_QobuzFlacVariants_ReturnsTrue(string existingPath, string targetQuality, bool expected)
    {
        var result = QualityHelper.ShouldUpgrade(existingPath, targetQuality);
        Assert.Equal(expected, result);
    }
}
