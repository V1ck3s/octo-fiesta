using octo_fiesta.Services.Qobuz;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;

namespace octo_fiesta.Tests;

public class QobuzMetadataServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<QobuzBundleService> _bundleServiceMock;
    private readonly Mock<ILogger<QobuzMetadataService>> _loggerMock;
    private readonly QobuzMetadataService _service;
    
    public QobuzMetadataServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        
        // Mock QobuzBundleService (methods are now virtual so can be mocked)
        var bundleHttpClientFactoryMock = new Mock<IHttpClientFactory>();
        bundleHttpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        var bundleLogger = Mock.Of<ILogger<QobuzBundleService>>();
        _bundleServiceMock = new Mock<QobuzBundleService>(bundleHttpClientFactoryMock.Object, bundleLogger) { CallBase = false };
        _bundleServiceMock.Setup(b => b.GetAppIdAsync()).ReturnsAsync("fake-app-id-12345");
        _bundleServiceMock.Setup(b => b.GetSecretsAsync()).ReturnsAsync(new List<string> { "fake-secret" });
        _bundleServiceMock.Setup(b => b.GetSecretAsync(It.IsAny<int>())).ReturnsAsync("fake-secret");
        
        _loggerMock = new Mock<ILogger<QobuzMetadataService>>();
        
        var subsonicSettings = Options.Create(new SubsonicSettings());
        var qobuzSettings = Options.Create(new QobuzSettings
        {
            UserAuthToken = "fake-user-auth-token",
            UserId = "8807208"
        });
        
        _service = new QobuzMetadataService(
            _httpClientFactoryMock.Object,
            subsonicSettings,
            qobuzSettings,
            _bundleServiceMock.Object,
            _loggerMock.Object);
    }
    
    #region SearchPlaylistsAsync Tests
    
    [Fact]
    public async Task SearchPlaylistsAsync_WithValidQuery_ReturnsPlaylists()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""playlists"": {
                    ""items"": [
                        {
                            ""id"": 1578664,
                            ""name"": ""Jazz Classics"",
                            ""description"": ""Best of classic jazz music"",
                            ""tracks_count"": 50,
                            ""duration"": 12000,
                            ""owner"": {
                                ""name"": ""Qobuz Editorial""
                            },
                            ""created_at"": 1609459200,
                            ""images300"": [""https://example.com/cover.jpg""]
                        }
                    ]
                }
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.SearchPlaylistsAsync("jazz", 20);
        
        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Jazz Classics", result[0].Name);
        Assert.Equal("Best of classic jazz music", result[0].Description);
        Assert.Equal(50, result[0].TrackCount);
        Assert.Equal(12000, result[0].Duration);
        Assert.Equal("qobuz", result[0].Provider);
        Assert.Equal("1578664", result[0].ExternalId);
        Assert.Equal("pl-qobuz-1578664", result[0].Id);
        Assert.Equal("Qobuz Editorial", result[0].CuratorName);
    }
    
    [Fact]
    public async Task SearchPlaylistsAsync_WithEmptyResults_ReturnsEmptyList()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""playlists"": {
                    ""items"": []
                }
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.SearchPlaylistsAsync("nonexistent", 20);
        
        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }
    
    [Fact]
    public async Task SearchPlaylistsAsync_WhenHttpFails_ReturnsEmptyList()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.InternalServerError
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.SearchPlaylistsAsync("jazz", 20);
        
        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }
    
    #endregion
    
    #region GetPlaylistAsync Tests
    
    [Fact]
    public async Task GetPlaylistAsync_WithValidId_ReturnsPlaylist()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""id"": 1578664,
                ""name"": ""Best Of Jazz"",
                ""description"": ""Top jazz tracks"",
                ""tracks_count"": 100,
                ""duration"": 24000,
                ""owner"": {
                    ""name"": ""Qobuz Editor""
                },
                ""created_at"": 1609459200,
                ""image_rectangle"": [""https://example.com/cover-large.jpg""]
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetPlaylistAsync("qobuz", "1578664");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal("Best Of Jazz", result.Name);
        Assert.Equal("Top jazz tracks", result.Description);
        Assert.Equal(100, result.TrackCount);
        Assert.Equal(24000, result.Duration);
        Assert.Equal("pl-qobuz-1578664", result.Id);
        Assert.Equal("Qobuz Editor", result.CuratorName);
        Assert.Equal("https://example.com/cover-large.jpg", result.CoverUrl);
    }
    
    [Fact]
    public async Task GetPlaylistAsync_WithWrongProvider_ReturnsNull()
    {
        // Act
        var result = await _service.GetPlaylistAsync("deezer", "12345");
        
        // Assert
        Assert.Null(result);
    }
    
    #endregion
    
    #region GetPlaylistTracksAsync Tests
    
    [Fact]
    public async Task GetPlaylistTracksAsync_WithValidId_ReturnsTracks()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""id"": 1578664,
                ""name"": ""My Jazz Playlist"",
                ""tracks"": {
                    ""items"": [
                        {
                            ""id"": 123456789,
                            ""title"": ""Take Five"",
                            ""duration"": 324,
                            ""track_number"": 1,
                            ""media_number"": 1,
                            ""performer"": {
                                ""id"": 111,
                                ""name"": ""Dave Brubeck Quartet""
                            },
                            ""album"": {
                                ""id"": 222,
                                ""title"": ""Time Out"",
                                ""artist"": {
                                    ""id"": 111,
                                    ""name"": ""Dave Brubeck Quartet""
                                },
                                ""image"": {
                                    ""thumbnail"": ""https://example.com/time-out.jpg""
                                }
                            }
                        },
                        {
                            ""id"": 987654321,
                            ""title"": ""So What"",
                            ""duration"": 562,
                            ""track_number"": 2,
                            ""media_number"": 1,
                            ""performer"": {
                                ""id"": 333,
                                ""name"": ""Miles Davis""
                            },
                            ""album"": {
                                ""id"": 444,
                                ""title"": ""Kind of Blue"",
                                ""artist"": {
                                    ""id"": 333,
                                    ""name"": ""Miles Davis""
                                },
                                ""image"": {
                                    ""thumbnail"": ""https://example.com/kind-of-blue.jpg""
                                }
                            }
                        }
                    ]
                }
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetPlaylistTracksAsync("qobuz", "1578664");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        
        // First track
        Assert.Equal("Take Five", result[0].Title);
        Assert.Equal("Dave Brubeck Quartet", result[0].Artist);
        Assert.Equal("My Jazz Playlist", result[0].Album); // Album should be playlist name
        Assert.Equal(1, result[0].Track); // Track index starts at 1
        Assert.Equal("ext-qobuz-song-123456789", result[0].Id);
        Assert.Equal("qobuz", result[0].ExternalProvider);
        Assert.Equal("123456789", result[0].ExternalId);
        
        // Second track
        Assert.Equal("So What", result[1].Title);
        Assert.Equal("Miles Davis", result[1].Artist);
        Assert.Equal("My Jazz Playlist", result[1].Album); // Album should be playlist name
        Assert.Equal(2, result[1].Track); // Track index increments
        Assert.Equal("ext-qobuz-song-987654321", result[1].Id);
    }
    
    [Fact]
    public async Task GetPlaylistTracksAsync_WithWrongProvider_ReturnsEmptyList()
    {
        // Act
        var result = await _service.GetPlaylistTracksAsync("deezer", "12345");
        
        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }
    
    [Fact]
    public async Task GetPlaylistTracksAsync_WhenHttpFails_ReturnsEmptyList()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.NotFound
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetPlaylistTracksAsync("qobuz", "999999");
        
        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }
    
    [Fact]
    public async Task GetPlaylistTracksAsync_WithMissingPlaylistName_UsesDefaultName()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""id"": 1578664,
                ""tracks"": {
                    ""items"": [
                        {
                            ""id"": 123,
                            ""title"": ""Test Track"",
                            ""performer"": {
                                ""id"": 1,
                                ""name"": ""Test Artist""
                            },
                            ""album"": {
                                ""id"": 2,
                                ""title"": ""Test Album"",
                                ""artist"": {
                                    ""id"": 1,
                                    ""name"": ""Test Artist""
                                }
                            }
                        }
                    ]
                }
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetPlaylistTracksAsync("qobuz", "1578664");
        
        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Unknown Playlist", result[0].Album);
    }
    
    #endregion
    
    #region SearchSongsAsync Tests
    
    [Fact]
    public async Task SearchSongsAsync_WithValidQuery_ReturnsSongs()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""tracks"": {
                    ""items"": [
                        {
                            ""id"": 123456789,
                            ""title"": ""Take Five"",
                            ""duration"": 324,
                            ""track_number"": 1,
                            ""performer"": {
                                ""id"": 111,
                                ""name"": ""Dave Brubeck Quartet""
                            },
                            ""album"": {
                                ""id"": 222,
                                ""title"": ""Time Out"",
                                ""artist"": {
                                    ""id"": 111,
                                    ""name"": ""Dave Brubeck Quartet""
                                }
                            }
                        }
                    ]
                }
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.SearchSongsAsync("Take Five", 20);
        
        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Take Five", result[0].Title);
        Assert.Equal("Dave Brubeck Quartet", result[0].Artist);
    }
    
    #endregion
    
    #region SearchAlbumsAsync Tests
    
    [Fact]
    public async Task SearchAlbumsAsync_WithValidQuery_ReturnsAlbums()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""albums"": {
                    ""items"": [
                        {
                            ""id"": 222,
                            ""title"": ""Time Out"",
                            ""tracks_count"": 7,
                            ""artist"": {
                                ""id"": 111,
                                ""name"": ""Dave Brubeck Quartet""
                            },
                            ""release_date_original"": ""1959-12-14""
                        }
                    ]
                }
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.SearchAlbumsAsync("Time Out", 20);
        
        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Time Out", result[0].Title);
        Assert.Equal("Dave Brubeck Quartet", result[0].Artist);
        Assert.Equal(1959, result[0].Year);
    }
    
    #endregion
    
    #region GetSongAsync Tests
    
    [Fact]
    public async Task GetSongAsync_WithValidId_ReturnsSong()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""id"": 123456789,
                ""title"": ""Take Five"",
                ""duration"": 324,
                ""track_number"": 1,
                ""isrc"": ""USCO10300456"",
                ""copyright"": ""(P) 1959 Columbia Records"",
                ""performer"": {
                    ""id"": 111,
                    ""name"": ""Dave Brubeck Quartet""
                },
                ""composer"": {
                    ""id"": 999,
                    ""name"": ""Paul Desmond""
                },
                ""album"": {
                    ""id"": 222,
                    ""title"": ""Time Out"",
                    ""tracks_count"": 7,
                    ""release_date_original"": ""1959-12-14"",
                    ""artist"": {
                        ""id"": 111,
                        ""name"": ""Dave Brubeck Quartet""
                    },
                    ""genres_list"": [""Jazz"", ""Jazz→Cool Jazz""]
                }
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetSongAsync("qobuz", "123456789");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal("Take Five", result.Title);
        Assert.Equal("Dave Brubeck Quartet", result.Artist);
        Assert.Equal("Time Out", result.Album);
        Assert.Equal("USCO10300456", result.Isrc);
        Assert.Equal("℗ 1959 Columbia Records", result.Copyright);
        Assert.Equal(1959, result.Year);
        Assert.Equal("1959-12-14", result.ReleaseDate);
        Assert.Contains("Paul Desmond", result.Contributors);
        Assert.Equal("Jazz, Cool Jazz", result.Genre);
    }
    
    [Fact]
    public async Task GetSongAsync_WithWrongProvider_ReturnsNull()
    {
        // Act
        var result = await _service.GetSongAsync("deezer", "123456789");
        
        // Assert
        Assert.Null(result);
    }
    
    #endregion
    
    #region GetAlbumAsync Tests
    
    [Fact]
    public async Task GetAlbumAsync_WithValidId_ReturnsAlbumWithTracks()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"{
                ""id"": 222,
                ""title"": ""Time Out"",
                ""tracks_count"": 2,
                ""release_date_original"": ""1959-12-14"",
                ""artist"": {
                    ""id"": 111,
                    ""name"": ""Dave Brubeck Quartet""
                },
                ""genres_list"": [""Jazz""],
                ""tracks"": {
                    ""items"": [
                        {
                            ""id"": 1,
                            ""title"": ""Blue Rondo à la Turk"",
                            ""track_number"": 1,
                            ""performer"": {
                                ""id"": 111,
                                ""name"": ""Dave Brubeck Quartet""
                            },
                            ""album"": {
                                ""id"": 222,
                                ""title"": ""Time Out"",
                                ""artist"": {
                                    ""id"": 111,
                                    ""name"": ""Dave Brubeck Quartet""
                                }
                            }
                        },
                        {
                            ""id"": 2,
                            ""title"": ""Take Five"",
                            ""track_number"": 2,
                            ""performer"": {
                                ""id"": 111,
                                ""name"": ""Dave Brubeck Quartet""
                            },
                            ""album"": {
                                ""id"": 222,
                                ""title"": ""Time Out"",
                                ""artist"": {
                                    ""id"": 111,
                                    ""name"": ""Dave Brubeck Quartet""
                                }
                            }
                        }
                    ]
                }
            }")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetAlbumAsync("qobuz", "222");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal("Time Out", result.Title);
        Assert.Equal("Dave Brubeck Quartet", result.Artist);
        Assert.Equal(1959, result.Year);
        Assert.Equal(2, result.Songs.Count);
        Assert.Equal("Blue Rondo à la Turk", result.Songs[0].Title);
        Assert.Equal("Take Five", result.Songs[1].Title);
    }
    
    [Fact]
    public async Task GetAlbumAsync_WithWrongProvider_ReturnsNull()
    {
        // Act
        var result = await _service.GetAlbumAsync("deezer", "222");
        
        // Assert
        Assert.Null(result);
    }
    
    #endregion
}
