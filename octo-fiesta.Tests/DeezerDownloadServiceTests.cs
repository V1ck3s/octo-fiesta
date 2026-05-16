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

        var mapping = new LocalSongMapping
        {
            ExternalProvider = "deezer",
            ExternalId = "123456",
            LocalPath = existingPath,
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            DownloadedAt = DateTime.UtcNow,
            DownloadedQuality = "FLAC" // Same or higher quality, so no upgrade needed
        };

        _localLibraryServiceMock
            .Setup(s => s.GetMappingForExternalSongAsync("deezer", "123456"))
            .ReturnsAsync(mapping);

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

    [Fact]
    public async Task GetTrackDownloadInfo_WhenMediaIsEmptyAndAlternativeExists_DoesNotDeadlock()
    {
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                var url = req.RequestUri!.ToString();
                string body;

                if (url.Contains("deezer.getUserData"))
                {
                    body = "{\"results\":{\"checkForm\":\"form\",\"USER\":{\"OPTIONS\":{\"license_token\":\"lic\"}}}}";
                }
                else if (url.EndsWith("/track/123") || url.EndsWith("/track/456"))
                {
                    body = "{\"title\":\"S\",\"artist\":{\"name\":\"A\"},\"readable\":true,\"track_token\":\"tok\"}";
                }
                else if (url.Contains("pageTrack"))
                {
                    body = "{\"results\":{\"DATA\":{\"FALLBACK\":{\"SNG_ID\":\"456\"},\"ISRC\":\"FOO\",\"TRACK_TOKEN\":\"pt\"}}}";
                }
                else if (url.Contains("get_url"))
                {
                    body = "{\"data\":[{}]}";
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected URL in mock: {url}");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                });
            });

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("deezer", "123"))
            .ReturnsAsync(new Song { ExternalId = "123", ExternalProvider = "deezer", Title = "T" });
        _localLibraryServiceMock
            .Setup(s => s.GetMappingForExternalSongAsync("deezer", "123"))
            .ReturnsAsync((LocalSongMapping?)null);

        var service = CreateService(arl: "test-arl");

        // Race against a timeout so a deadlock surfaces as a test failure rather than hanging the runner.
        var downloadTask = service.DownloadSongAsync("deezer", "123");
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
        var winner = await Task.WhenAny(downloadTask, timeoutTask);

        Assert.True(winner == downloadTask,
            "DownloadSongAsync did not complete within 5 seconds, likely deadlocked on _requestLock.");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => downloadTask);
        Assert.Contains("No media sources", ex.Message);
    }
}
