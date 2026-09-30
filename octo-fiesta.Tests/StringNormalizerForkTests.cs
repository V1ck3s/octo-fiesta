using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

public class StringNormalizerForkTests
{
    [Fact]
    public void CreateSongTitleDedupeKey_StripsTrailingFeatParens()
    {
        Assert.Equal("boi", StringNormalizer.CreateSongTitleDedupeKey("Boi (feat. Butch Dawson)"));
    }

    [Fact]
    public void CreateSongTitleDedupeKey_StripsTrailingFeatBrackets()
    {
        Assert.Equal(
            "praise the lord (da shine)",
            StringNormalizer.CreateSongTitleDedupeKey("Praise The Lord (Da Shine) [feat. Skepta]"));
    }

    [Fact]
    public void CreateSongTitleDedupeKey_LeavesRemasteredSuffix()
    {
        Assert.Equal("song (remastered)", StringNormalizer.CreateSongTitleDedupeKey("Song (Remastered)"));
    }

    [Fact]
    public void CreateSongTitleDedupeKey_MatchesPlainAndFeatTitle()
    {
        Assert.Equal(
            StringNormalizer.CreateSongTitleDedupeKey("Boi"),
            StringNormalizer.CreateSongTitleDedupeKey("Boi (feat. Butch Dawson)"));
    }

    [Fact]
    public void CreateSongTitleDedupeKey_WithMixedCase_ReturnsDistinctKeys()
    {
        Assert.Equal(
            StringNormalizer.CreateSongTitleDedupeKey("Boi"),
            StringNormalizer.CreateSongTitleDedupeKey("boi"));
    }
}
