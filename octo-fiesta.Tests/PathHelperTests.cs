using octo_fiesta.Services.Common;
using Xunit;

namespace octo_fiesta.Tests;

public class PathHelperUnitTests
{
    #region GetInvalidFileNameChars Tests

    [Fact]
    public void GetInvalidFileNameChars_WithNull_ReturnsCurrentOSChars()
    {
        var result = PathHelper.GetInvalidFileNameChars(null);
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GetInvalidFileNameChars_WithWindows_ReturnsWindowsChars()
    {
        var result = PathHelper.GetInvalidFileNameChars("Windows");
        Assert.Contains('"', result);
        Assert.Contains('<', result);
        Assert.Contains('>', result);
        Assert.Contains('|', result);
        Assert.Contains(':', result);
        Assert.Contains('*', result);
        Assert.Contains('?', result);
        Assert.Contains('\\', result);
        Assert.Contains('/', result);
    }

    [Fact]
    public void GetInvalidFileNameChars_WithUnix_ReturnsUnixChars()
    {
        var result = PathHelper.GetInvalidFileNameChars("Unix");
        Assert.Equal(2, result.Length);
        Assert.Contains('\0', result);
        Assert.Contains('/', result);
    }

    [Fact]
    public void GetInvalidFileNameChars_WithEmptyString_ReturnsCurrentOSChars()
    {
        var result = PathHelper.GetInvalidFileNameChars("");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    #endregion

    #region GetInvalidPathChars Tests

    [Fact]
    public void GetInvalidPathChars_WithNull_ReturnsCurrentOSChars()
    {
        var result = PathHelper.GetInvalidPathChars(null);
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GetInvalidPathChars_WithWindows_ReturnsWindowsChars()
    {
        var result = PathHelper.GetInvalidPathChars("Windows");
        Assert.Contains('|', result);
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('*', result);
        Assert.DoesNotContain('?', result);
    }

    [Fact]
    public void GetInvalidPathChars_WithUnix_ReturnsUnixChars()
    {
        var result = PathHelper.GetInvalidPathChars("Unix");
        Assert.Single(result);
        Assert.Contains('\0', result);
    }

    [Fact]
    public void GetInvalidPathChars_WithEmptyString_ReturnsCurrentOSChars()
    {
        var result = PathHelper.GetInvalidPathChars("");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    #endregion

    #region GetCachePath Tests

    [Fact]
    public void GetCachePath_ReturnsPathWithCacheSubfolder()
    {
        var result = PathHelper.GetCachePath();
        Assert.EndsWith("octo-fiesta-cache", result);
    }

    [Fact]
    public void GetCachePath_ReturnsAbsolutePath()
    {
        var result = PathHelper.GetCachePath();
        Assert.True(Path.IsPathRooted(result));
    }

    #endregion

    #region BuildTrackPath Tests

    [Fact]
    public void BuildTrackPath_WithValidInputs_ReturnsCorrectPath()
    {
        var result = PathHelper.BuildTrackPath("/music", "Artist", "Album", "Song", 5, ".flac");
        Assert.EndsWith("Artist/Album/05 - Song.flac", result);
    }

    [Fact]
    public void BuildTrackPath_WithoutTrackNumber_ReturnsPathWithoutPrefix()
    {
        var result = PathHelper.BuildTrackPath("/music", "Artist", "Album", "Song", null, ".mp3");
        Assert.EndsWith("Artist/Album/Song.mp3", result);
    }

    [Fact]
    public void BuildTrackPath_WithSpecialCharacters_SanitizesNames()
    {
        var result = PathHelper.BuildTrackPath("/music", "Artist", "Album", "Song/Name", 1, ".flac");
        var fileName = Path.GetFileName(result);
        Assert.DoesNotContain("/", fileName);
    }

    [Fact]
    public void BuildTrackPath_WithNullTrackNumber_SanitizesFolderNames()
    {
        var result = PathHelper.BuildTrackPath("/music", "Artist", "Album", "Song", null, ".flac");
        Assert.Contains("Artist", result);
        Assert.Contains("Album", result);
    }

    [Fact]
    public void BuildTrackPath_WithExtensionWithoutDot_AddsDot()
    {
        var result = PathHelper.BuildTrackPath("/music", "Artist", "Album", "Song", 1, "flac");
        Assert.EndsWith(".flac", result);
    }

    [Fact]
    public void BuildTrackPath_WithEmptyExtension_ReturnsNoDot()
    {
        var result = PathHelper.BuildTrackPath("/music", "Artist", "Album", "Song", 1, "");
        Assert.EndsWith("Song", result);
    }

    #endregion

    #region SanitizeFileName Tests

    [Fact]
    public void SanitizeFileName_WithValidName_ReturnsSameName()
    {
        var result = PathHelper.SanitizeFileName("Valid Song Name");
        Assert.Equal("Valid Song Name", result);
    }

    [Fact]
    public void SanitizeFileName_WithWindows_ReplacesWindowsInvalidChars()
    {
        var result = PathHelper.SanitizeFileName("Song:Name?Test<File>Name", "Windows");
        Assert.DoesNotContain(":", result);
        Assert.DoesNotContain("?", result);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
        Assert.Contains("_", result);
    }

    [Fact]
    public void SanitizeFileName_WithUnix_ReplacesUnixInvalidChars()
    {
        var result = PathHelper.SanitizeFileName("Song/Name", "Unix");
        Assert.DoesNotContain("/", result);
        Assert.Contains("_", result);
    }

    [Fact]
    public void SanitizeFileName_WithLongName_TruncatesTo100Chars()
    {
        var longName = new string('a', 150);
        var result = PathHelper.SanitizeFileName(longName);
        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void SanitizeFileName_WithLeadingTrailingSpaces_Trims()
    {
        var result = PathHelper.SanitizeFileName("  Song Name  ");
        Assert.Equal("Song Name", result);
    }

    #endregion

    #region SanitizeFolderName Tests

    [Fact]
    public void SanitizeFolderName_WithValidName_ReturnsSameName()
    {
        var result = PathHelper.SanitizeFolderName("Valid Folder");
        Assert.Equal("Valid Folder", result);
    }

    [Fact]
    public void SanitizeFolderName_WithWindows_ReplacesInvalidChars()
    {
        var result = PathHelper.SanitizeFolderName("Folder:Name?Test<File>|Name", "Windows");
        Assert.DoesNotContain(":", result);
        Assert.DoesNotContain("?", result);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
        Assert.DoesNotContain("|", result);
    }

    [Fact]
    public void SanitizeFolderName_WithUnix_ReplacesInvalidChars()
    {
        var result = PathHelper.SanitizeFolderName("Folder/Name", "Unix");
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void SanitizeFolderName_WithLongName_TruncatesTo100Chars()
    {
        var longName = new string('a', 150);
        var result = PathHelper.SanitizeFolderName(longName);
        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void SanitizeFolderName_WithLeadingTrailingDots_Trims()
    {
        var result = PathHelper.SanitizeFolderName("...FolderName...");
        Assert.False(result.StartsWith("."));
        Assert.False(result.EndsWith("."));
    }

    [Fact]
    public void SanitizeFolderName_WithLeadingTrailingSpaces_Trims()
    {
        var result = PathHelper.SanitizeFolderName("  FolderName  ");
        Assert.Equal("FolderName", result);
    }

    #endregion

    #region ResolveUniquePath Tests

    [Fact]
    public void ResolveUniquePath_WithNonExistingPath_ReturnsSamePath()
    {
        var result = PathHelper.ResolveUniquePath("/nonexistent/path/song.flac");
        Assert.Equal("/nonexistent/path/song.flac", result);
    }

    [Fact]
    public void ResolveUniquePath_WithExistingPath_AppendsCounter()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.txt");
        try
        {
            File.WriteAllText(tempFile, "test");
            var result = PathHelper.ResolveUniquePath(tempFile);
            Assert.NotEqual(tempFile, result);
            Assert.Contains("(1)", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveUniquePath_WithMultipleExistingPaths_IncrementsCounter()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.txt");
        var tempFile2 = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.txt");
        try
        {
            File.WriteAllText(tempFile, "test");
            File.WriteAllText(tempFile2, "test");
            var result = PathHelper.ResolveUniquePath(tempFile);
            Assert.Contains("(1)", result);
            Assert.DoesNotContain("(2)", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
            if (File.Exists(tempFile2))
                File.Delete(tempFile2);
        }
    }

    #endregion

    #region OsFormat Parameter Tests

    [Fact]
    public void SanitizeFileName_PassesOsFormatToGetInvalidFileNameChars()
    {
        var windowsResult = PathHelper.SanitizeFileName("a:b", "Windows");
        var unixResult = PathHelper.SanitizeFileName("a:b", "Unix");
        
        Assert.DoesNotContain(":", windowsResult);
        Assert.Contains(":", unixResult);
    }

    [Fact]
    public void SanitizeFolderName_PassesOsFormatToInvalidCharsMethods()
    {
        var windowsResult = PathHelper.SanitizeFolderName("a:b", "Windows");
        var unixResult = PathHelper.SanitizeFolderName("a:b", "Unix");
        
        Assert.DoesNotContain(":", windowsResult);
        Assert.Contains(":", unixResult);
    }

    #endregion
}
