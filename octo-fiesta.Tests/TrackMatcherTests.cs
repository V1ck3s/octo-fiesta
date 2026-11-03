using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

public class TrackMatcherTests
{
    [Theory]
    [InlineData("Say You Will (Album Version)", "Say You Will")]
    [InlineData("Gold Digger (Album Version (Explicit))", "Gold Digger")]
    [InlineData("Mercy (Album Version (Explicit))", "Mercy")]
    [InlineData("Come Together - Remastered 2009", "Come Together")]
    [InlineData("Come Together (2019 Remaster)", "Come Together")]
    [InlineData("Song [Explicit]", "Song")]
    [InlineData("Song (feat. Someone, Other)", "Song")]
    [InlineData("Song feat. Someone", "Song")]
    [InlineData("Diamonds From Sierra Leone (Remix - Album Version (Explicit))", "Diamonds From Sierra Leone - Remix")]
    [InlineData("JAŸ-Z Song", "Jay-Z Song")]
    public void CreateTitleKey_EditionVariants_AreEqual(string providerTitle, string libraryTitle)
    {
        Assert.Equal(TrackMatcher.CreateTitleKey(libraryTitle), TrackMatcher.CreateTitleKey(providerTitle));
    }

    [Theory]
    [InlineData("Runaway (Piano Cover)", "Runaway")]
    [InlineData("Heartless (Acoustic)", "Heartless")]
    [InlineData("Stronger (Live)", "Stronger")]
    [InlineData("Diamonds From Sierra Leone - Remix", "Diamonds From Sierra Leone")]
    [InlineData("Pinocchio Story (Freestyle / Live From Singapore)", "Pinocchio Story")]
    [InlineData("runaway (slowed + reverb)", "Runaway")]
    public void CreateTitleKey_DifferentRecordings_AreNotEqual(string a, string b)
    {
        Assert.NotEqual(TrackMatcher.CreateTitleKey(b), TrackMatcher.CreateTitleKey(a));
    }

    [Fact]
    public void CleanTitle_KeepsCaseAndDropsLabels()
    {
        Assert.Equal("Say You Will", TrackMatcher.CleanTitle("Say You Will (Album Version)"));
    }

    [Theory]
    [InlineData("Kanye West", "Kanye West")]
    [InlineData("Kanye West, Kid Cudi", "Kanye West")]
    [InlineData("Kanye West feat. Jamie Foxx", "Kanye West")]
    [InlineData("Simon & Garfunkel", "Simon")]
    public void PrimaryArtist_ReturnsFirstCredit(string artist, string expected)
    {
        Assert.Equal(expected, TrackMatcher.PrimaryArtist(artist));
    }

    [Theory]
    [InlineData("Say You Will (Album Version)", "Kanye West", "Say You Will", "Kanye West")]
    [InlineData("Welcome To Heartbreak (Album Version)", "Kanye West", "Welcome To Heartbreak", "Kanye West, Kid Cudi")]
    [InlineData("Gold Digger (Album Version (Explicit))", "Kanye West", "Gold Digger", "Kanye West, Jamie Foxx")]
    [InlineData("No Church In The Wild", "JAY-Z", "No Church In The Wild", "JAŸ-Z, Kanye West")]
    [InlineData("Otis", "Kanye West, JAY-Z", "Otis", "JAY-Z")]
    public void IsMatch_SameRecordingWithDifferentTags_ReturnsTrue(string extTitle, string extArtist, string libTitle, string libArtist)
    {
        Assert.True(TrackMatcher.IsMatch(new TrackInfo(extTitle, extArtist), new TrackInfo(libTitle, libArtist)));
    }

    [Theory]
    [InlineData("Runaway", "Kanye West", "Runaway (Piano Cover)", "Solar Flare Piano")]
    [InlineData("Runaway (Piano Cover)", "Solar Flare Piano", "Runaway", "Kanye West")]
    [InlineData("Heartless", "Kanye West", "Heartless", "The Fray")]
    [InlineData("Heartless", "Kanye West", "Heartless (Acoustic)", "Kanye West")]
    public void IsMatch_DifferentRecording_ReturnsFalse(string extTitle, string extArtist, string libTitle, string libArtist)
    {
        Assert.False(TrackMatcher.IsMatch(new TrackInfo(extTitle, extArtist), new TrackInfo(libTitle, libArtist)));
    }

    [Fact]
    public void IsMatch_LooseMatchWithDifferentDuration_ReturnsFalse()
    {
        var external = new TrackInfo("Song (Album Version)", "Artist", Duration: 200);
        Assert.False(TrackMatcher.IsMatch(external, new TrackInfo("Song", "Artist", Duration: 120)));
        Assert.True(TrackMatcher.IsMatch(external, new TrackInfo("Song", "Artist", Duration: 203)));
    }

    [Fact]
    public void IsMatch_IdenticalTags_IgnoresDuration()
    {
        Assert.True(TrackMatcher.IsMatch(new TrackInfo("Song", "Artist", Duration: 200), new TrackInfo("Song", "Artist", Duration: 120)));
    }

    [Fact]
    public void IsMatch_SameIsrc_ReturnsTrueDespiteDifferentTags()
    {
        var external = new TrackInfo("Titre", "Artiste", Isrc: "USUM70812345");
        var candidate = new TrackInfo("Title", "Artist", IsrcList: new[] { "usum70812345" });
        Assert.True(TrackMatcher.IsMatch(external, candidate));
    }

    [Fact]
    public void IsMatch_UsesOpenSubsonicArtistsArray()
    {
        var candidate = new TrackInfo("Song", "Main Artist", ArtistNames: new[] { "Main Artist", "Guest" });
        Assert.True(TrackMatcher.IsMatch(new TrackInfo("Song (Explicit)", "Guest"), candidate));
    }
}
