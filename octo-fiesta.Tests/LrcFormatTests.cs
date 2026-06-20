using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Lyrics;

namespace octo_fiesta.Tests;

public class LrcFormatTests
{
    [Fact]
    public void ParseSynced_ParsesTimestampsToMilliseconds()
    {
        var lrc = "[00:12.34]Hello\n[01:05.00]World";

        var lines = LrcFormat.ParseSynced(lrc);

        Assert.Equal(2, lines.Count);
        Assert.Equal(12_340, lines[0].StartMs);
        Assert.Equal("Hello", lines[0].Text);
        Assert.Equal(65_000, lines[1].StartMs);
        Assert.Equal("World", lines[1].Text);
    }

    [Fact]
    public void ParseSynced_NormalizesTwoAndThreeDigitFractions()
    {
        // 2-digit fraction = centiseconds, 3-digit = milliseconds.
        var lines = LrcFormat.ParseSynced("[00:01.5]a\n[00:02.05]b\n[00:03.123]c");

        Assert.Equal(1_500, lines[0].StartMs);
        Assert.Equal(2_050, lines[1].StartMs);
        Assert.Equal(3_123, lines[2].StartMs);
    }

    [Fact]
    public void ParseSynced_ExpandsRepeatedTimestampsAndSortsByTime()
    {
        var lines = LrcFormat.ParseSynced("[00:30.00][00:10.00]chorus");

        Assert.Equal(2, lines.Count);
        Assert.Equal(10_000, lines[0].StartMs);
        Assert.Equal(30_000, lines[1].StartMs);
        Assert.All(lines, l => Assert.Equal("chorus", l.Text));
    }

    [Fact]
    public void ParseSynced_IgnoresMetadataTagsAndBlankLines()
    {
        var lrc = "[ar:Artist]\n[ti:Title]\n[length:03:21]\n[00:00.00]first line";

        var lines = LrcFormat.ParseSynced(lrc);

        Assert.Single(lines);
        Assert.Equal("first line", lines[0].Text);
    }

    [Fact]
    public void ParseSynced_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(LrcFormat.ParseSynced(null));
        Assert.Empty(LrcFormat.ParseSynced(""));
    }

    [Fact]
    public void ParsePlain_SplitsLinesWithZeroTimestamps()
    {
        var lines = LrcFormat.ParsePlain("line one\r\nline two");

        Assert.Equal(2, lines.Count);
        Assert.Equal("line one", lines[0].Text);
        Assert.All(lines, l => Assert.Equal(0, l.StartMs));
    }

    [Fact]
    public void ToLrc_SyncedLyrics_RoundTripsThroughParser()
    {
        var lyrics = new SongLyrics
        {
            DisplayArtist = "Daft Punk",
            DisplayTitle = "Get Lucky",
            Synced = true,
            Lines =
            {
                new LyricLine(12_340, "Hello"),
                new LyricLine(65_000, "World")
            }
        };

        var lrc = LrcFormat.ToLrc(lyrics);
        var reparsed = LrcFormat.ParseSynced(lrc);

        Assert.Contains("[ar:Daft Punk]", lrc);
        Assert.Contains("[ti:Get Lucky]", lrc);
        Assert.Equal(2, reparsed.Count);
        Assert.Equal(12_340, reparsed[0].StartMs);
        Assert.Equal("Hello", reparsed[0].Text);
        Assert.Equal(65_000, reparsed[1].StartMs);
    }
}
