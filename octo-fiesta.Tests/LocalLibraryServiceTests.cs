using octo_fiesta.Services;
using octo_fiesta.Models;
using Microsoft.Extensions.Configuration;

namespace octo_fiesta.Tests;

public class LocalLibraryServiceTests : IDisposable
{
    private readonly LocalLibraryService _service;
    private readonly string _testDownloadPath;

    public LocalLibraryServiceTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        _service = new LocalLibraryService(configuration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    [Fact]
    public void ParseSongId_WithExternalId_ReturnsCorrectParts()
    {
        // Act
        var (isExternal, provider, externalId) = _service.ParseSongId("ext-deezer-123456");

        // Assert
        Assert.True(isExternal);
        Assert.Equal("deezer", provider);
        Assert.Equal("123456", externalId);
    }

    [Fact]
    public void ParseSongId_WithLocalId_ReturnsNotExternal()
    {
        // Act
        var (isExternal, provider, externalId) = _service.ParseSongId("local-789");

        // Assert
        Assert.False(isExternal);
        Assert.Null(provider);
        Assert.Null(externalId);
    }

    [Fact]
    public void ParseSongId_WithNumericId_ReturnsNotExternal()
    {
        // Act
        var (isExternal, provider, externalId) = _service.ParseSongId("12345");

        // Assert
        Assert.False(isExternal);
        Assert.Null(provider);
        Assert.Null(externalId);
    }

    [Fact]
    public async Task GetLocalPathForExternalSongAsync_WhenNotRegistered_ReturnsNull()
    {
        // Act
        var result = await _service.GetLocalPathForExternalSongAsync("deezer", "nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RegisterDownloadedSongAsync_ThenGetLocalPath_ReturnsPath()
    {
        // Arrange
        var song = new Song
        {
            Id = "ext-deezer-123456",
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            ExternalProvider = "deezer",
            ExternalId = "123456"
        };
        var localPath = Path.Combine(_testDownloadPath, "test-song.mp3");
        
        // Create the file
        await File.WriteAllTextAsync(localPath, "fake audio content");

        // Act
        await _service.RegisterDownloadedSongAsync(song, localPath);
        var result = await _service.GetLocalPathForExternalSongAsync("deezer", "123456");

        // Assert
        Assert.Equal(localPath, result);
    }

    [Fact]
    public async Task GetLocalPathForExternalSongAsync_WhenFileDeleted_ReturnsNull()
    {
        // Arrange
        var song = new Song
        {
            Id = "ext-deezer-999999",
            Title = "Deleted Song",
            Artist = "Test Artist",
            Album = "Test Album",
            ExternalProvider = "deezer",
            ExternalId = "999999"
        };
        var localPath = Path.Combine(_testDownloadPath, "deleted-song.mp3");
        
        // Create and then delete the file
        await File.WriteAllTextAsync(localPath, "fake audio content");
        await _service.RegisterDownloadedSongAsync(song, localPath);
        File.Delete(localPath);

        // Act
        var result = await _service.GetLocalPathForExternalSongAsync("deezer", "999999");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RegisterDownloadedSongAsync_WithNullProvider_DoesNothing()
    {
        // Arrange
        var song = new Song
        {
            Id = "local-123",
            Title = "Local Song",
            Artist = "Local Artist",
            Album = "Local Album",
            ExternalProvider = null,
            ExternalId = null
        };
        var localPath = Path.Combine(_testDownloadPath, "local-song.mp3");

        // Act - should not throw
        await _service.RegisterDownloadedSongAsync(song, localPath);

        // Assert - nothing to assert, just checking it doesn't throw
        Assert.True(true);
    }
}
