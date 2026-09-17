using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

/// <summary>
/// Cases are seeded with real Qobuz playlist/search responses captured on
/// 2026-09-07 against a live library, not with invented data.
/// </summary>
public class PlaylistRelevanceFilterTests
{
    private static ExternalPlaylist Playlist(string name, string? curator = "Qobuz France")
        => new() { Id = "pl-qobuz-1", Name = name, CuratorName = curator, Provider = "qobuz" };

    // "gainsbourg": every playlist Qobuz returned was genuinely about Gainsbourg.
    [Fact]
    public void Apply_WhenEveryPlaylistMatches_KeepsThemAll()
    {
        var playlists = new List<ExternalPlaylist>
        {
            Playlist("Serge Gainsbourg"),
            Playlist("Gainsbourg, par les autres..."),
            Playlist("Gainsbourg's Got Class(ique)", "What the France"),
            Playlist("Charlotte Gainsbourg"),
            Playlist("Les interprètes de Gainsbourg")
        };

        var result = PlaylistRelevanceFilter.Apply("gainsbourg", playlists);

        Assert.Equal(5, result.Count);
    }

    // "keen v": Qobuz padded the response with five unrelated playlists.
    [Fact]
    public void Apply_WhenNothingMatches_DropsThePadding()
    {
        var playlists = new List<ExternalPlaylist>
        {
            Playlist("Protoje"),
            Playlist("Kim Wilde"),
            Playlist("HiFi ROSE", "HiFi ROSE"),
            Playlist("Les classiques du Reggae"),
            Playlist("Ella Fitzgerald")
        };

        var result = PlaylistRelevanceFilter.Apply("keen v", playlists);

        Assert.Empty(result);
    }

    // "columbine": Qobuz matched loosely on "colum", which is not the query.
    [Fact]
    public void Apply_WithLooseProviderSubstringMatches_DropsThem()
    {
        var playlists = new List<ExternalPlaylist>
        {
            Playlist("Les classiques de Colombie"),
            Playlist("100 ans de Columbia Pictures"),
            Playlist("Columbia"),
            Playlist("Colemine Records")
        };

        var result = PlaylistRelevanceFilter.Apply("columbine", playlists);

        Assert.Empty(result);
    }

    // A multi-word query must match on ALL its words, or "Serge Gainsbourg"
    // would keep the four chanson-francaise fillers Qobuz returned alongside it.
    [Fact]
    public void Apply_WithMultiWordQuery_RequiresEveryWord()
    {
        var playlists = new List<ExternalPlaylist>
        {
            Playlist("Serge Gainsbourg"),
            Playlist("Hi-Res Masters : Classiques de la Chanson Française"),
            Playlist("Chanson française - Années 80")
        };

        var result = PlaylistRelevanceFilter.Apply("Serge Gainsbourg", playlists);

        Assert.Single(result);
        Assert.Equal("Serge Gainsbourg", result[0].Name);
    }

    [Fact]
    public void Apply_IgnoresCaseAndDiacritics()
    {
        var playlists = new List<ExternalPlaylist> { Playlist("Les interprètes de Gainsbourg") };

        var result = PlaylistRelevanceFilter.Apply("INTERPRETES", playlists);

        Assert.Single(result);
    }

    // The query may carry punctuation the playlist name spells differently.
    [Fact]
    public void Apply_IgnoresPunctuationOnBothSides()
    {
        var playlists = new List<ExternalPlaylist> { Playlist("Gainsbourg's Got Class(ique)", "What the France") };

        var result = PlaylistRelevanceFilter.Apply("gainsbourg got class", playlists);

        Assert.Single(result);
    }

    // Searching the curator is legitimate: it is how you find a label's shelf.
    [Fact]
    public void Apply_MatchesOnCuratorName()
    {
        var playlists = new List<ExternalPlaylist> { Playlist("Hi-Res Masters : Jazz", "What the France") };

        var result = PlaylistRelevanceFilter.Apply("what the france", playlists);

        Assert.Single(result);
    }

    [Fact]
    public void Apply_WithBlankQuery_LeavesTheListUntouched()
    {
        var playlists = new List<ExternalPlaylist> { Playlist("Protoje") };

        var result = PlaylistRelevanceFilter.Apply("   ", playlists);

        Assert.Single(result);
    }

    [Fact]
    public void Apply_WithNoPlaylists_ReturnsEmpty()
    {
        var result = PlaylistRelevanceFilter.Apply("gainsbourg", new List<ExternalPlaylist>());

        Assert.Empty(result);
    }

    // A playlist with no curator must not blow up the haystack build.
    [Fact]
    public void Apply_WithNullCurator_StillMatchesOnName()
    {
        var playlists = new List<ExternalPlaylist> { Playlist("Serge Gainsbourg", null) };

        var result = PlaylistRelevanceFilter.Apply("gainsbourg", playlists);

        Assert.Single(result);
    }
}
