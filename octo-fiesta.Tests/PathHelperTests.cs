using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Common;
using Xunit;

namespace octo_fiesta.Tests;

public class PathHelperTests : IDisposable
{
    private static readonly string Sep = Path.DirectorySeparatorChar.ToString();
    private const string DefaultTemplate = "{artist}/{album}/{track} - {title}";
    private readonly string _testPath;

    public PathHelperTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-pathhelper-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPath))
        {
            Directory.Delete(_testPath, true);
        }
    }

    #region Template-based BuildTrackPath

    [Fact]
    public void BuildTrackPath_DefaultTemplate_MatchesLegacyBehavior()
    {
        var song = new Song
        {
            Title = "Everything in Its Right Place",
            Artist = "Radiohead",
            Album = "Kid A",
            Track = 1
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac", DefaultTemplate, "FLAC");

        Assert.Equal($"/downloads{Sep}Radiohead{Sep}Kid A{Sep}01 - Everything in Its Right Place.flac", result);
    }

    [Fact]
    public void BuildTrackPath_DefaultTemplate_UsesAlbumArtistWhenAvailable()
    {
        var song = new Song
        {
            Title = "My Song",
            Artist = "Track Artist",
            AlbumArtist = "Album Artist",
            Album = "My Album",
            Track = 1
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac", DefaultTemplate, null);

        Assert.Equal($"/downloads{Sep}Album Artist{Sep}My Album{Sep}01 - My Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_CustomTemplate_WithYearAndQuality()
    {
        var song = new Song
        {
            Title = "Idioteque",
            Artist = "Radiohead",
            Album = "Kid A",
            Track = 8,
            Year = 2000
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{artist}/[{year}] {album} ({quality})/{track} - {title}", "FLAC");

        Assert.Equal($"/downloads{Sep}Radiohead{Sep}[2000] Kid A (FLAC){Sep}08 - Idioteque.flac", result);
    }

    [Fact]
    public void BuildTrackPath_CustomTemplate_WithDiscNumber()
    {
        var song = new Song
        {
            Title = "My Song",
            Artist = "Artist",
            Album = "Album",
            Track = 3,
            DiscNumber = 2
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{artist}/{album}/{disc}-{track} - {title}", "FLAC");

        Assert.Equal($"/downloads{Sep}Artist{Sep}Album{Sep}2-03 - My Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_CustomTemplate_WithGenre()
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Track = 1,
            Genre = "Alternative"
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{genre}/{artist}/{album}/{track} - {title}", null);

        Assert.Equal($"/downloads{Sep}Alternative{Sep}Artist{Sep}Album{Sep}01 - Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_NullYear_ReplacesWithUnknown()
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Track = 1,
            Year = null
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{artist}/[{year}] {album}/{track} - {title}", "FLAC");

        Assert.Equal($"/downloads{Sep}Artist{Sep}[Unknown] Album{Sep}01 - Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_NullGenre_ReplacesWithUnknown()
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Track = 1,
            Genre = null
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{genre}/{artist}/{track} - {title}", null);

        Assert.Equal($"/downloads{Sep}Unknown{Sep}Artist{Sep}01 - Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_NullQuality_ReplacesWithUnknown()
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Track = 1
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{artist}/{album} ({quality})/{track} - {title}", null);

        Assert.Equal($"/downloads{Sep}Artist{Sep}Album (Unknown){Sep}01 - Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_NullDiscNumber_ReplacesWithUnknown()
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Track = 1,
            DiscNumber = null
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{artist}/{album}/Disc {disc}/{track} - {title}", null);

        Assert.Equal($"/downloads{Sep}Artist{Sep}Album{Sep}Disc Unknown{Sep}01 - Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_NoTrackNumber_CleansUpDashPrefix()
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Track = null
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{artist}/{album}/{track} - {title}", null);

        Assert.Equal($"/downloads{Sep}Artist{Sep}Album{Sep}Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_FlatTemplate_NoFolderSegments()
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Track = 1
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{artist} - {album} - {track} - {title}", null);

        Assert.Equal($"/downloads{Sep}Artist - Album - 01 - Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_SpecialCharactersInMetadata_Sanitized()
    {
        var song = new Song
        {
            Title = "What's My Age Again?",
            Artist = "AC/DC",
            Album = "Who Made Who: Disc 1",
            Track = 5
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac", DefaultTemplate, null);

        // '/' and '?' and ':' should be replaced with '_'
        Assert.Contains("AC_DC", result);
        Assert.Contains("Who Made Who_ Disc 1", result);
        Assert.Contains("What's My Age Again_", result);
    }

    [Fact]
    public void BuildTrackPath_AllPlaceholders_AllResolved()
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            AlbumArtist = "AlbumArtist",
            Album = "Album",
            Track = 3,
            DiscNumber = 2,
            Year = 2023,
            Genre = "Rock"
        };

        var result = PathHelper.BuildTrackPath("/downloads", song, ".flac",
            "{artist}/[{year}] {album} ({quality})/Disc {disc}/{track} - {title} [{genre}]", "FLAC_24");

        Assert.Equal(
            $"/downloads{Sep}AlbumArtist{Sep}[2023] Album (FLAC_24){Sep}Disc 2{Sep}03 - Song [Rock].flac",
            result);
    }

    #endregion

    #region SanitizeFileName

    [Theory]
    [InlineData("Normal Name", "Normal Name")]
    [InlineData("Name/With/Slashes", "Name_With_Slashes")]
    [InlineData("Name:With:Colons", "Name_With_Colons")]
    [InlineData("Name*With*Stars", "Name_With_Stars")]
    [InlineData("", "Unknown")]
    [InlineData("   ", "Unknown")]
    public void SanitizeFileName_ReplacesInvalidChars(string input, string expected)
    {
        Assert.Equal(expected, PathHelper.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_TruncatesAt100Characters()
    {
        var longName = new string('A', 150);
        var result = PathHelper.SanitizeFileName(longName);
        Assert.Equal(100, result.Length);
    }

    #endregion

    #region SanitizeFolderName

    [Theory]
    [InlineData("Normal Name", "Normal Name")]
    [InlineData("Name.", "Name")]
    [InlineData("...Name...", "...Name")]
    [InlineData("", "Unknown")]
    [InlineData("   ", "Unknown")]
    [InlineData("Name/With/Slashes", "Name_With_Slashes")]
    public void SanitizeFolderName_HandlesEdgeCases(string input, string expected)
    {
        Assert.Equal(expected, PathHelper.SanitizeFolderName(input));
    }

    [Fact]
    public void SanitizeFolderName_TruncatesAt100Characters()
    {
        var longName = new string('A', 150);
        var result = PathHelper.SanitizeFolderName(longName);
        Assert.Equal(100, result.Length);
    }

    #endregion

    #region ResolveUniquePath Tests

    [Fact]
    public void ResolveUniquePath_WhenFileDoesNotExist_ReturnsSamePath()
    {
        // Arrange
        var path = Path.Combine(_testPath, "nonexistent.mp3");

        // Act
        var result = PathHelper.ResolveUniquePath(path);

        // Assert
        Assert.Equal(path, result);
    }

    [Fact]
    public void ResolveUniquePath_WhenFileExists_ReturnsPathWithCounter()
    {
        // Arrange
        var basePath = Path.Combine(_testPath, "existing.mp3");
        File.WriteAllText(basePath, "content");

        // Act
        var result = PathHelper.ResolveUniquePath(basePath);

        // Assert
        Assert.NotEqual(basePath, result);
        Assert.Contains("existing (1).mp3", result);
    }

    [Fact]
    public void ResolveUniquePath_WhenMultipleFilesExist_IncrementsCounter()
    {
        // Arrange
        var basePath = Path.Combine(_testPath, "song.mp3");
        var path1 = Path.Combine(_testPath, "song (1).mp3");
        File.WriteAllText(basePath, "content");
        File.WriteAllText(path1, "content");

        // Act
        var result = PathHelper.ResolveUniquePath(basePath);

        // Assert
        Assert.Contains("song (2).mp3", result);
    }

    #endregion
}
