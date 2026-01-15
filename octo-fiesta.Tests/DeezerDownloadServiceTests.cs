using octo_fiesta.Services;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Common;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace octo_fiesta.Tests;

public class DeezerDownloadServiceTests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<ILocalLibraryService> _localLibraryServiceMock;
    private readonly Mock<IMusicMetadataService> _metadataServiceMock;
    private readonly Mock<ILogger<DeezerDownloadService>> _loggerMock;
    private readonly IConfiguration _configuration;
    private readonly string _testDownloadPath;

    public DeezerDownloadServiceTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-download-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);

        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        _localLibraryServiceMock = new Mock<ILocalLibraryService>();
        _metadataServiceMock = new Mock<IMusicMetadataService>();
        _loggerMock = new Mock<ILogger<DeezerDownloadService>>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath,
                ["Deezer:Arl"] = null,
                ["Deezer:ArlFallback"] = null
            })
            .Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    private DeezerDownloadService CreateService(string? arl = null, DownloadMode downloadMode = DownloadMode.Track)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath,
                ["Deezer:Arl"] = arl,
                ["Deezer:ArlFallback"] = null
            })
            .Build();

        var subsonicSettings = Options.Create(new SubsonicSettings 
        { 
            DownloadMode = downloadMode 
        });
        
        var deezerSettings = Options.Create(new DeezerSettings
        {
            Arl = arl,
            ArlFallback = null,
            Quality = null
        });

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(octo_fiesta.Services.Subsonic.PlaylistSyncService)))
            .Returns(null);

        return new DeezerDownloadService(
            _httpClientFactoryMock.Object,
            config,
            _localLibraryServiceMock.Object,
            _metadataServiceMock.Object,
            subsonicSettings,
            deezerSettings,
            serviceProviderMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task IsAvailableAsync_WithoutArl_ReturnsFalse()
    {
        // Arrange
        var service = CreateService(arl: null);

        // Act
        var result = await service.IsAvailableAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithEmptyArl_ReturnsFalse()
    {
        // Arrange
        var service = CreateService(arl: "");

        // Act
        var result = await service.IsAvailableAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DownloadSongAsync_WithUnsupportedProvider_ThrowsNotSupportedException()
    {
        // Arrange
        var service = CreateService(arl: "test-arl");

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => 
            service.DownloadSongAsync("spotify", "123456"));
    }

    [Fact]
    public async Task DownloadSongAsync_WhenAlreadyDownloaded_ReturnsExistingPath()
    {
        // Arrange
        var existingPath = Path.Combine(_testDownloadPath, "existing-song.mp3");
        await File.WriteAllTextAsync(existingPath, "fake audio content");

        _localLibraryServiceMock
            .Setup(s => s.GetLocalPathForExternalSongAsync("deezer", "123456"))
            .ReturnsAsync(existingPath);

        var service = CreateService(arl: "test-arl");

        // Act
        var result = await service.DownloadSongAsync("deezer", "123456");

        // Assert
        Assert.Equal(existingPath, result);
    }

    [Fact]
    public void GetDownloadStatus_WithUnknownSongId_ReturnsNull()
    {
        // Arrange
        var service = CreateService(arl: "test-arl");

        // Act
        var result = service.GetDownloadStatus("unknown-id");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenSongNotFound_ThrowsException()
    {
        // Arrange
        _localLibraryServiceMock
            .Setup(s => s.GetLocalPathForExternalSongAsync("deezer", "999999"))
            .ReturnsAsync((string?)null);

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("deezer", "999999"))
            .ReturnsAsync((Song?)null);

        var service = CreateService(arl: "test-arl");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Exception>(() => 
            service.DownloadSongAsync("deezer", "999999"));
        
        Assert.Equal("Song not found", exception.Message);
    }

    [Fact]
    public void DownloadRemainingAlbumTracksInBackground_WithUnsupportedProvider_DoesNotThrow()
    {
        // Arrange
        var service = CreateService(arl: "test-arl", downloadMode: DownloadMode.Album);

        // Act & Assert - Should not throw, just log warning
        service.DownloadRemainingAlbumTracksInBackground("spotify", "123456", "789");
    }

    [Fact]
    public void DownloadRemainingAlbumTracksInBackground_WithDeezerProvider_StartsBackgroundTask()
    {
        // Arrange
        _metadataServiceMock
            .Setup(s => s.GetAlbumAsync("deezer", "123456"))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-123456",
                Title = "Test Album",
                Songs = new List<Song>
                {
                    new Song { ExternalId = "111", Title = "Track 1" },
                    new Song { ExternalId = "222", Title = "Track 2" }
                }
            });

        var service = CreateService(arl: "test-arl", downloadMode: DownloadMode.Album);

        // Act - Should not throw (fire-and-forget)
        service.DownloadRemainingAlbumTracksInBackground("deezer", "123456", "111");
        
        // Assert - Just verify it doesn't throw, actual download is async
        Assert.True(true);
    }
}

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
