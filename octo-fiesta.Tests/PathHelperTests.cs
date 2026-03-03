using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

/// <summary>
/// Unit tests for the PathHelper class that handles file organization logic.
/// </summary>
public class PathHelperTests : IDisposable
{
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

    #region SanitizeFileName Tests

    [Fact]
    public void SanitizeFileName_WithValidName_ReturnsUnchanged()
    {
        // Arrange & Act
        var result = PathHelper.SanitizeFileName("My Song Title");

        // Assert
        Assert.Equal("My Song Title", result);
    }

    [Fact]
    public void SanitizeFileName_WithInvalidChars_ReplacesWithUnderscore()
    {
        // Arrange - Use forward slash which is invalid on all platforms
        var result = PathHelper.SanitizeFileName("Song/With/Invalid");

        // Assert - Check that forward slashes were replaced with underscores
        Assert.Equal("Song_With_Invalid", result);
    }

    [Fact]
    public void SanitizeFileName_WithNullOrEmpty_ReturnsUnknown()
    {
        // Arrange & Act
        var resultNull = PathHelper.SanitizeFileName(null!);
        var resultEmpty = PathHelper.SanitizeFileName("");
        var resultWhitespace = PathHelper.SanitizeFileName("   ");

        // Assert
        Assert.Equal("Unknown", resultNull);
        Assert.Equal("Unknown", resultEmpty);
        Assert.Equal("Unknown", resultWhitespace);
    }

    [Fact]
    public void SanitizeFileName_WithLongName_TruncatesTo100Chars()
    {
        // Arrange
        var longName = new string('A', 150);

        // Act
        var result = PathHelper.SanitizeFileName(longName);

        // Assert
        Assert.Equal(100, result.Length);
    }

    #endregion

    #region SanitizeFolderName Tests

    [Fact]
    public void SanitizeFolderName_WithValidName_ReturnsUnchanged()
    {
        // Arrange & Act
        var result = PathHelper.SanitizeFolderName("Artist Name");

        // Assert
        Assert.Equal("Artist Name", result);
    }

    [Fact]
    public void SanitizeFolderName_WithNullOrEmpty_ReturnsUnknown()
    {
        // Arrange & Act
        var resultNull = PathHelper.SanitizeFolderName(null!);
        var resultEmpty = PathHelper.SanitizeFolderName("");
        var resultWhitespace = PathHelper.SanitizeFolderName("   ");

        // Assert
        Assert.Equal("Unknown", resultNull);
        Assert.Equal("Unknown", resultEmpty);
        Assert.Equal("Unknown", resultWhitespace);
    }

    [Fact]
    public void SanitizeFolderName_WithTrailingDots_RemovesDots()
    {
        // Arrange & Act
        var result = PathHelper.SanitizeFolderName("Artist Name...");

        // Assert
        Assert.Equal("Artist Name", result);
    }

    [Fact]
    public void SanitizeFolderName_WithInvalidChars_ReplacesWithUnderscore()
    {
        // Arrange - Use forward slash which is invalid on all platforms
        var result = PathHelper.SanitizeFolderName("Artist/With/Invalid");

        // Assert - Check that forward slashes were replaced with underscores
        Assert.Equal("Artist_With_Invalid", result);
    }

    #endregion

    #region BuildTrackPath Tests

    [Fact]
    public void BuildTrackPath_WithAllParameters_CreatesCorrectStructure()
    {
        // Arrange
        var downloadPath = "/downloads";
        var artist = "Test Artist";
        var album = "Test Album";
        var title = "Test Song";
        var trackNumber = 5;
        var extension = ".mp3";

        // Act
        var result = PathHelper.BuildTrackPath(downloadPath, artist, album, title, trackNumber, extension);

        // Assert
        Assert.Contains("Test Artist", result);
        Assert.Contains("Test Album", result);
        Assert.Contains("05 - Test Song.mp3", result);
    }

    [Fact]
    public void BuildTrackPath_WithoutTrackNumber_OmitsTrackPrefix()
    {
        // Arrange
        var downloadPath = "/downloads";
        var artist = "Test Artist";
        var album = "Test Album";
        var title = "Test Song";
        var extension = ".mp3";

        // Act
        var result = PathHelper.BuildTrackPath(downloadPath, artist, album, title, null, extension);

        // Assert
        Assert.Contains("Test Song.mp3", result);
        Assert.DoesNotContain(" - Test Song", result.Split(Path.DirectorySeparatorChar).Last());
    }

    [Fact]
    public void BuildTrackPath_WithSingleDigitTrack_PadsWithZero()
    {
        // Arrange & Act
        var result = PathHelper.BuildTrackPath("/downloads", "Artist", "Album", "Song", 3, ".mp3");

        // Assert
        Assert.Contains("03 - Song.mp3", result);
    }

    [Fact]
    public void BuildTrackPath_WithFlacExtension_UsesFlacExtension()
    {
        // Arrange & Act
        var result = PathHelper.BuildTrackPath("/downloads", "Artist", "Album", "Song", 1, ".flac");

        // Assert
        Assert.EndsWith(".flac", result);
    }

    [Fact]
    public void BuildTrackPath_CreatesArtistAlbumHierarchy()
    {
        // Arrange & Act
        var result = PathHelper.BuildTrackPath("/downloads", "My Artist", "My Album", "My Song", 1, ".mp3");

        // Assert
        // Verify the structure is: downloadPath/Artist/Album/track.mp3
        var parts = result.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Contains("My Artist", parts);
        Assert.Contains("My Album", parts);

        // Artist should come before Album in the path
        var artistIndex = Array.IndexOf(parts, "My Artist");
        var albumIndex = Array.IndexOf(parts, "My Album");
        Assert.True(artistIndex < albumIndex, "Artist folder should be parent of Album folder");
    }

    [Fact]
    public void BuildTrackPath_UserSubfoldersDisabled_IgnoresUsername()
    {
        var result = PathHelper.BuildTrackPath(
            "/downloads",
            "Radiohead",
            "Kid A",
            "Everything in Its Right Place",
            1,
            ".flac",
            userSubfolders: false,
            username: "alice");

        Assert.Contains(Path.Combine("/downloads", "Radiohead", "Kid A"), result);
        Assert.DoesNotContain(Path.Combine("/downloads", "alice"), result);
    }

    [Fact]
    public void BuildTrackPath_UserSubfoldersEnabled_PrependsUsername()
    {
        var result = PathHelper.BuildTrackPath(
            "/downloads",
            "Radiohead",
            "Kid A",
            "Everything in Its Right Place",
            1,
            ".flac",
            userSubfolders: true,
            username: "alice");

        Assert.Contains(Path.Combine("/downloads", "alice", "Radiohead", "Kid A"), result);
    }

    [Fact]
    public void BuildTrackPath_UserSubfoldersEnabled_NullUsername_FallsBackToRoot()
    {
        var result = PathHelper.BuildTrackPath(
            "/downloads",
            "Radiohead",
            "Kid A",
            "Everything in Its Right Place",
            1,
            ".flac",
            userSubfolders: true,
            username: null);

        Assert.Contains(Path.Combine("/downloads", "Radiohead", "Kid A"), result);
    }

    [Fact]
    public void BuildTrackPath_UserSubfoldersEnabled_SanitizesUsername()
    {
        var result = PathHelper.BuildTrackPath(
            "/downloads",
            "Artist",
            "Album",
            "Track",
            1,
            ".flac",
            userSubfolders: true,
            username: "../evil");

        Assert.StartsWith("/downloads/", result);
        Assert.DoesNotContain("..", result);
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
