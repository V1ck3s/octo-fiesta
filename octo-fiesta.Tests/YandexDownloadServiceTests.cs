using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using octo_fiesta.Services.Yandex;

namespace octo_fiesta.Tests;

public class YandexDownloadServiceTests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<ILocalLibraryService> _localLibraryServiceMock;
    private readonly Mock<IMusicMetadataService> _metadataServiceMock;
    private readonly Mock<ILogger<YandexDownloadService>> _loggerMock;
    private readonly IConfiguration _configuration;
    private readonly string _testDownloadPath;

    public YandexDownloadServiceTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-yandex-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);

        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object)
        {
            BaseAddress = new Uri("https://example.org")
        };
        
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        _localLibraryServiceMock = new Mock<ILocalLibraryService>();
        _metadataServiceMock = new Mock<IMusicMetadataService>();

        _loggerMock = new Mock<ILogger<YandexDownloadService>>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
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

    private YandexDownloadService CreateService(
        string? oAuthToken = null, 
        string? quality = null,
        bool IncludeUnavailable = false,
        string language = "ru",
        DownloadMode downloadMode = DownloadMode.Track)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        var subsonicSettings = Options.Create(new SubsonicSettings 
        { 
            DownloadMode = downloadMode 
        });
        
        var yandexSettings = Options.Create(new YandexSettings
        {
            OAuthToken = oAuthToken,
            Quality = quality,
            IncludeUnavailable = IncludeUnavailable,
            Language = language,
        });

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(octo_fiesta.Services.Subsonic.PlaylistSyncService)))
            .Returns(null!);

        return new YandexDownloadService(
            _httpClientFactoryMock.Object,
            config,
            _localLibraryServiceMock.Object,
            _metadataServiceMock.Object,
            subsonicSettings,
            yandexSettings,
            serviceProviderMock.Object,
            _loggerMock.Object);
    }

    #region IsAvailableAsync Tests

    [Fact]
    public async Task IsAvailableAsync_WithoutOAuthToken_ReturnsFalse()
    {
        // Arrange
        var service = CreateService(oAuthToken: null);

        // Act
        var result = await service.IsAvailableAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithEmptyToken_ReturnsFalse()
    {
        // Arrange
        var service = CreateService(oAuthToken: "");

        // Act
        var result = await service.IsAvailableAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithValidToken_WithSubscription_ReturnsTrue()
    {
        // Arrange
        // Mock a successful response for account subscription
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""{"result": {"plus": {"hasPlus": true}}}""")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/account/status")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        var service = CreateService(oAuthToken: "test-token");

        // Act
        var result = await service.IsAvailableAsync();

        // Assert - Will be false because bundle extraction will fail with our mock, but service is constructed
        Assert.True(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithValidToken_WithoutSubscription_ReturnsTrue()
    {
        // Arrange
        // Mock a successful response for account subscription
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""{"result": {"plus": {"hasPlus": false}}}""")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/account/status")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        var service = CreateService(oAuthToken: "test-token");

        // Act
        var result = await service.IsAvailableAsync();

        // Assert - Will be false because bundle extraction will fail with our mock, but service is constructed
        Assert.False(result);
    }

    #endregion

    #region DownloadSongAsync Tests

    [Fact]
    public async Task DownloadSongAsync_WithUnsupportedProvider_ThrowsNotSupportedException()
    {
        // Arrange
        var service = CreateService(oAuthToken: "test-token");

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => 
            service.DownloadSongAsync("spotify", "123456"));
    }

    [Fact]
    public async Task DownloadSongAsync_WhenAlreadyDownloaded_ReturnsExistingPath()
    {
        // Arrange
        var existingPath = Path.Combine(_testDownloadPath, "existing-song.flac");
        await File.WriteAllTextAsync(existingPath, "fake audio content");

        var mapping = new LocalSongMapping
        {
            ExternalProvider = "yandex",
            ExternalId = "123456",
            LocalPath = existingPath,
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            DownloadedAt = DateTime.UtcNow,
            DownloadedQuality = "FLAC" // Same or higher quality, so no upgrade needed
        };

        _localLibraryServiceMock
            .Setup(s => s.GetMappingForExternalSongAsync("yandex", "123456"))
            .ReturnsAsync(mapping);

        // service tries to get available options for download – here we mock them
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""
            {
                "result": {
                    "downloadInfo": {
                        "bitrate": 320,
                        "codec": "mp3",
                        "quality": "lossless",
                        "key": "abracadabra",
                        "url": "https://example.org/mp3",
                        "urls": [
                            "https://example.org/mp3",
                            "https://example.org/mp3-2"
                        ]
                    }
                }
            }
            """)
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/get-file-info/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);

        var service = CreateService(oAuthToken: "test-token");

        // Act
        var result = await service.DownloadSongAsync("yandex", "123456");

        // Assert
        Assert.Equal(existingPath, result);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenModernApiErrors_DownloadsFromLegacyApi()
    {
        // Arrange

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("yandex", "123456"))
            .ReturnsAsync(new Song
            {
                Title = "Test Song",
                Artist = "Test Artist",
                ArtistId = "ext-yandex-artist-123456",
                Album = "Test Album",
                AlbumId = "ext-yandex-album-123456",
                Duration = 15,
                IsLocal = false,
                ExternalProvider = "yandex",
                ExternalId = "123456"
            });

        var mockResponseModernApi = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""
            {
                "result": {
                    "name": "track-download-info-error",
                    "message": "no-rights"
                }
            }
            """)
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/get-file-info/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseModernApi);


        var mockResponseLegacyApi = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""
            {
                "result": [
                    {
                        "codec": "mp3",
                        "downloadInfoUrl": "https://example.org/get-mp3",
                        "bitrateInKbps": 128
                    }
                ]
            }
            """)
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/download-info")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseLegacyApi);
        
        var mockResponseLegacyDownloadInfo = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""
            <download-info>
                <host>get-example-file.org</host>
                <path>/tes-test
            </path>
                <ts>19cde9597e0</ts>
                <s>abracadabra</s>
            </download-info>
            """)
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("https://example.org/get-mp3")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseLegacyDownloadInfo);
        
        string testSongContent = "test-song-content";
        var mockResponseLegacyDownloadTrack = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(testSongContent)
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("get-example-file.org")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseLegacyDownloadTrack);



        var service = CreateService(oAuthToken: "test-token");

        // Act
        var resultPath = await service.DownloadSongAsync("yandex", "123456");
        string result = File.ReadAllText(resultPath);

        // Assert
        Assert.Equal(testSongContent, result);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenModernApiSucceeds_DownloadsSong()
    {
        // Arrange

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("yandex", "123456"))
            .ReturnsAsync(new Song
            {
                Title = "Test Song",
                Artist = "Test Artist",
                ArtistId = "ext-yandex-artist-123456",
                Album = "Test Album",
                AlbumId = "ext-yandex-album-123456",
                Duration = 15,
                IsLocal = false,
                ExternalProvider = "yandex",
                ExternalId = "123456"
            });

        var mockResponseModernApi = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""
            {
                "result": {
                    "downloadInfo": {
                        "quality": "lossless",
                        "codec": "mp3",
                        "bitrate": 320,
                        "key": "112233aabbcc445566ddeeff77889900",
                        "url": "https://example-download.org/320.mp3",
                        "urls": [
                            "https://example-download.org/320.mp3",
                            "https://example-download.org/320-2.mp3"
                        ]
                    }
                }
            }
            """)
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/get-file-info/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseModernApi);

        // Raw and AES-CTR-encrypted value
        // https://www.devglan.com/online-tools/aes-encryption-decryption
        string rawContent = "test-song-content";
        byte[] encryptedContent = Convert.FromHexString("3C56F92EE5A764673E6F713DD20CD3C8EF");

        var mockResponseDownload = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new ByteArrayContent(encryptedContent)
        };


        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("https://example-download.org")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseDownload);
        

        var service = CreateService(oAuthToken: "test-token");

        // Act
        var resultPath = await service.DownloadSongAsync("yandex", "123456");
        string result = File.ReadAllText(resultPath);

        // Assert
        Assert.Equal(rawContent, result);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenSongNotFound_ThrowsException()
    {
        // Arrange
        _localLibraryServiceMock
            .Setup(s => s.GetLocalPathForExternalSongAsync("yandex", "999999"))
            .ReturnsAsync((string?)null);

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("yandex", "999999"))
            .ReturnsAsync((Song?)null);

        var service = CreateService(oAuthToken: "test-token");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Exception>(() => 
            service.DownloadSongAsync("yandex", "999999"));
        
        Assert.Equal("Song not found", exception.Message);
    }

    #endregion

    #region DownloadSongAsync Quality Format Tests

    [Fact]
    public async Task DownloadSongAsync_WithFlacQuality_RequestsFlac()
    {
        // Arrange
        var service = CreateService(
            oAuthToken: "test-token",
            quality: "FLAC");

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("yandex", "123456"))
            .ReturnsAsync(new Song
            {
                Title = "Test Song",
                IsLocal = false,
                ExternalProvider = "yandex",
                ExternalId = "123456"
            });

        // Act
        try
        {
            await service.DownloadSongAsync("yandex", "123456");
        }
        catch (System.Exception) {}

        // Assert
        // Requests flac
        _httpMessageHandlerMock.Protected()
            .Verify(
                "SendAsync",
                Times.AtLeastOnce(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null
                    && req.RequestUri.Query.Contains("codecs=flac")),
                ItExpr.IsAny<CancellationToken>()
            );
        // Doesn't request other formats
        _httpMessageHandlerMock.Protected()
            .Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null
                    && (
                        req.RequestUri.Query.Contains("mp3")
                        || req.RequestUri.Query.Contains("aac-mp4")
                    )
                ),
                ItExpr.IsAny<CancellationToken>()
            );
    }

    [Fact]
    public async Task DownloadSongAsync_WithAac64Quality_RequestsHeAac()
    {
        // Arrange
        var service = CreateService(
            oAuthToken: "test-token",
            quality: "AAC_64");

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("yandex", "123456"))
            .ReturnsAsync(new Song
            {
                Title = "Test Song",
                IsLocal = false,
                ExternalProvider = "yandex",
                ExternalId = "123456"
            });

        // Act
        try
        {
            await service.DownloadSongAsync("yandex", "123456");
        }
        catch (System.Exception) {}

        // Assert
        // Requests he-aac
        _httpMessageHandlerMock.Protected()
            .Verify(
                "SendAsync",
                Times.AtLeastOnce(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null
                    && req.RequestUri.Query.Contains("codecs=he-aac")),
                ItExpr.IsAny<CancellationToken>()
            );
        // Doesn't request other formats
        _httpMessageHandlerMock.Protected()
            .Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null
                    && (
                        req.RequestUri.Query.Contains("mp3")
                        || req.RequestUri.Query.Contains("flac")
                    )
                ),
                ItExpr.IsAny<CancellationToken>()
            );
    }


    [Fact]
    public async Task DownloadSongAsync_WithNullQuality_RequestsFlac()
    {
        // Arrange
        var service = CreateService(
            oAuthToken: "test-token",
            quality: null);

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("yandex", "123456"))
            .ReturnsAsync(new Song
            {
                Title = "Test Song",
                IsLocal = false,
                ExternalProvider = "yandex",
                ExternalId = "123456"
            });

        // Act
        try
        {
            await service.DownloadSongAsync("yandex", "123456");
        }
        catch (System.Exception) {}

        // Assert
        // Requests flac
        _httpMessageHandlerMock.Protected()
            .Verify(
                "SendAsync",
                Times.AtLeastOnce(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null
                    && req.RequestUri.Query.Contains("codecs=flac")),
                ItExpr.IsAny<CancellationToken>()
            );
        // Doesn't request other formats
        _httpMessageHandlerMock.Protected()
            .Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null
                    && (
                        req.RequestUri.Query.Contains("mp3")
                        || req.RequestUri.Query.Contains("aac-mp4")
                    )
                ),
                ItExpr.IsAny<CancellationToken>()
            );
    }

    #endregion


    #region GetDownloadStatus Tests

    [Fact]
    public void GetDownloadStatus_WithUnknownSongId_ReturnsNull()
    {
        // Arrange
        var service = CreateService(oAuthToken: "test-token");

        // Act
        var result = service.GetDownloadStatus("unknown-id");

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Album Download Tests

    [Fact]
    public void DownloadRemainingAlbumTracksInBackground_WithUnsupportedProvider_DoesNotThrow()
    {
        // Arrange
        var service = CreateService(
            oAuthToken: "test-token", 
            downloadMode: DownloadMode.Album);

        // Act & Assert - Should not throw, just log warning
        service.DownloadRemainingAlbumTracksInBackground("spotify", "123456", "789");
    }

    [Fact]
    public void DownloadRemainingAlbumTracksInBackground_WithYandexProvider_StartsBackgroundTask()
    {
        // Arrange
        _metadataServiceMock
            .Setup(s => s.GetAlbumAsync("yandex", "123456"))
            .ReturnsAsync(new Album
            {
                Id = "ext-yandex-album-123456",
                Title = "Test Album",
                Songs = new List<Song>
                {
                    new Song { ExternalId = "111", Title = "Track 1" },
                    new Song { ExternalId = "222", Title = "Track 2" }
                }
            });

        var service = CreateService(
            oAuthToken: "test-token", 
            downloadMode: DownloadMode.Album);

        // Act
        service.DownloadRemainingAlbumTracksInBackground(
            "yandex",
            "123456",
            "111"
        );

        // Assert - Verify with timeout that background task performs a call to GetAlbumAsync
        // Meaning that background task successfully started
        int millisecondsTimeout = 2000;
        int waited = 0;
        bool expectationMet = false;
        MockException? exception = null;

        while (waited < millisecondsTimeout && !expectationMet)
        {
            try
            {
                _metadataServiceMock.Verify(mock => mock.GetAlbumAsync("yandex", "123456"), Times.AtLeastOnce);
                expectationMet = true;
            }
            catch (MockException ex)
            {
                exception = ex;
            }
            waited += 50;
            Thread.Sleep(50);
        }

        if (!expectationMet)
        {
            throw exception!;
        }
        
    }

    #endregion

    #region ExtractExternalIdFromAlbumId Tests

    [Fact]
    public void ExtractExternalIdFromAlbumId_WithValidYandexAlbumId_ReturnsExternalId()
    {
        // Arrange
        var service = CreateService(oAuthToken: "test-token");
        var albumId = "ext-yandex-album-60253780838";
        // The method is protected, so we use Reflection to test it
        var extractExternalIdFromAlbumIdMethod = service.GetType().GetMethod(
            "ExtractExternalIdFromAlbumId",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic
        );

        // Act
        var externalAlbumId = (string?) extractExternalIdFromAlbumIdMethod?.Invoke(service, [albumId]);

        // Assert
        Assert.Equal("60253780838", externalAlbumId);
    }

    [Fact]
    public void ExtractExternalIdFromAlbumId_WithInvalidYandexAlbumId_ReturnsNull()
    {
        // Arrange
        var service = CreateService(oAuthToken: "test-token");
        var albumId = "ext-deezer-album-60253780838";
        // The method is protected, so we use Reflection to test it
        var extractExternalIdFromAlbumIdMethod = service.GetType().GetMethod(
            "ExtractExternalIdFromAlbumId",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic
        );

        // Act
        var result = (string?) extractExternalIdFromAlbumIdMethod?.Invoke(service, [albumId]);

        // Assert
        Assert.Null(result);
    }

    #endregion

}
