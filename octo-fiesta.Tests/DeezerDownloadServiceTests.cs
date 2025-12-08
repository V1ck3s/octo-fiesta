using octo_fiesta.Services;
using octo_fiesta.Models;
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

    private DeezerDownloadService CreateService(string? arl = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath,
                ["Deezer:Arl"] = arl,
                ["Deezer:ArlFallback"] = null
            })
            .Build();

        return new DeezerDownloadService(
            _httpClientFactoryMock.Object,
            config,
            _localLibraryServiceMock.Object,
            _metadataServiceMock.Object,
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
}
