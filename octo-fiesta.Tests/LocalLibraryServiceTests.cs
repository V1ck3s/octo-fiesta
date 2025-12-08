using octo_fiesta.Services;
using octo_fiesta.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;

namespace octo_fiesta.Tests;

public class LocalLibraryServiceTests : IDisposable
{
    private readonly LocalLibraryService _service;
    private readonly string _testDownloadPath;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;

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

        // Mock HttpClient
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"scanStatus\":{\"scanning\":false,\"count\":100}}}")
            });
        
        var httpClient = new HttpClient(mockHandler.Object);
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var subsonicSettings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });
        var mockLogger = new Mock<ILogger<LocalLibraryService>>();

        _service = new LocalLibraryService(configuration, _mockHttpClientFactory.Object, subsonicSettings, mockLogger.Object);
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

    [Fact]
    public async Task TriggerLibraryScanAsync_ReturnsTrue()
    {
        // Act
        var result = await _service.TriggerLibraryScanAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GetScanStatusAsync_ReturnsScanStatus()
    {
        // Act
        var result = await _service.GetScanStatusAsync();

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Scanning);
        Assert.Equal(100, result.Count);
    }

    [Theory]
    [InlineData("ext-deezer-123", true, "deezer", "123")]
    [InlineData("ext-spotify-abc123", true, "spotify", "abc123")]
    [InlineData("ext-tidal-999-888", true, "tidal", "999-888")]
    [InlineData("123456", false, null, null)]
    [InlineData("", false, null, null)]
    [InlineData("ext-", false, null, null)]
    [InlineData("ext-deezer", false, null, null)]
    public void ParseSongId_VariousInputs_ReturnsExpected(string songId, bool expectedIsExternal, string? expectedProvider, string? expectedExternalId)
    {
        // Act
        var (isExternal, provider, externalId) = _service.ParseSongId(songId);

        // Assert
        Assert.Equal(expectedIsExternal, isExternal);
        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedExternalId, externalId);
    }
}
