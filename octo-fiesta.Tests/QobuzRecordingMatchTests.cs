using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Tests;

public class QobuzRecordingMatchTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"isrc\":[\"OTHER\",\"GBTEST000001\"]}", true)]
    [InlineData("{\"isrc\":[]}", false)]
    [InlineData("{\"isrc\":\"OTHER\"}", false)]
    [InlineData("{\"isrc\":\"GBTEST000001\"}", true)]
    [InlineData("{\"isrc\":\"gbtest000001\",\"explicitStatus\":\"explicit\"}", true)]
    [InlineData("{\"isrc\":\"GBTEST000001\",\"explicitStatus\":\"clean\"}", false)]
    public void RequiresIdentifierAndRejectsConflictingAdvisory(string candidate, bool expected)
    {
        using var doc = JsonDocument.Parse(candidate);
        Assert.Equal(expected, LocalLibraryService.IsQobuzRecordingMatch(
            new Song { Isrc = "GBTEST000001", IsExplicit = true }, doc.RootElement));
        Assert.False(LocalLibraryService.IsQobuzRecordingMatch(new Song(), doc.RootElement));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchDoesNotHideQobuzVersionBehindUnidentifiedLocalSong(bool json)
    {
        var mapper = new octo_fiesta.Services.Subsonic.SubsonicModelMapper(
            new octo_fiesta.Services.Subsonic.SubsonicResponseBuilder(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<octo_fiesta.Services.Subsonic.SubsonicModelMapper>.Instance);
        var local = new Dictionary<string, object> { ["title"] = "Song", ["artist"] = "Artist" };
        var localSongs = new List<object> { json ? local : new System.Xml.Linq.XElement("song",
            new System.Xml.Linq.XAttribute("title", "Song"), new System.Xml.Linq.XAttribute("artist", "Artist")) };
        var result = mapper.MergeSearchResults(localSongs, new(), new(), new octo_fiesta.Models.Search.SearchResult
        {
            Songs = new() { new Song { Title = "Song", Artist = "Artist", ExternalProvider = "qobuz",
                ExternalId = "1", Isrc = "GBTEST000001", IsExplicit = true } }
        }, new(), json);
        Assert.Equal(2, result.MergedSongs.Count);
    }
}
