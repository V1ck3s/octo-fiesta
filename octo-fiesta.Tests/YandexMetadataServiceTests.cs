using octo_fiesta.Models.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using octo_fiesta.Services.Yandex;
using System.Text.Json;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Models.Yandex;
using octo_fiesta.Models.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace octo_fiesta.Tests;

public class YandexMetadataServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<ILogger<YandexMetadataService>> _loggerMock;
    private readonly YandexMetadataService _service;
    
    public YandexMetadataServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object)
        {
            BaseAddress = new Uri("https://example.org")
        };

        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        
        _loggerMock = new Mock<ILogger<YandexMetadataService>>();
        
        var subsonicSettings = Options.Create(new SubsonicSettings());
        
        var yandexSettings = Options.Create(new YandexSettings
        {
            OAuthToken = "oAuthToken",
            Quality = "AAC_256",
            IncludeUnavailable = false,
            Language = "ru",
        });
        
        _service = new YandexMetadataService(
            _httpClientFactoryMock.Object,
            yandexSettings,
            _loggerMock.Object);
    }
    
    #region SearchPlaylistsAsync Tests
    
    [Fact]
    public async Task SearchPlaylistsAsync_WithValidQuery_ReturnsPlaylists()
    {
        // Arrange
        var mockResponsePage0 = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""
            {
              "result": {
                "playlists": {
                  "results": [
                    {
                      "owner": {
                        "login": "music-blog",
                        "name": "Yandex Music"
                      },
                      "playlistUuid": "f3063016-5636-454e-916e-00a756e9b25d",
                      "available": true,
                      "title": "Rock Forever!",
                      "description": "The best rock tributes",
                      "trackCount": 109,
                      "created": "2019-07-17T19:58:07+00:00",
                      "durationMs": 28158630,
                      "ogImage": "get-cover-arts.example.org/very/long/path/filename%%?121231314"
                    }
                  ]
                }
              }
            }
            """)
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.Query.Contains("&page=0")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponsePage0);

        var mockResponsePage1 = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""{"result": {}}""")
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.Query.Contains("&page=1")),
                // ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponsePage1);

        // Act
        var result = await _service.SearchPlaylistsAsync("rock", 20);
        
        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Rock Forever!", result[0].Name);
        Assert.Equal("The best rock tributes", result[0].Description);
        Assert.Equal(109, result[0].TrackCount);
        Assert.Equal(28158, result[0].Duration);
        Assert.Equal("yandex", result[0].Provider);
        Assert.Equal("f3063016-5636-454e-916e-00a756e9b25d", result[0].ExternalId);
        Assert.Equal("pl-yandex-f3063016-5636-454e-916e-00a756e9b25d", result[0].Id);
        Assert.Equal("Yandex Music", result[0].CuratorName);
    }
    
    [Fact]
    public async Task SearchPlaylistsAsync_WithEmptyResults_ReturnsEmptyList()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""{"result": {"playlists": {"results": []}}}""")
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
            Content = new StringContent("""
            {
                "result": {
                    "owner": {
                        "login": "music-blog"
                    },
                    "playlistUuid": "f3063016-5636-454e-916e-00a756e9b25d",
                    "available": true,
                    "title": "Rock Forever!",
                    "description": "The best rock tributes",
                    "trackCount": 109,
                    "created": "2019-07-17T19:58:07+00:00",
                    "durationMs": 28158630,
                    "ogImage": "get-cover-arts.example.org/very/long/path/filename%%?121231314"
                }
            }
            """)
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetPlaylistAsync("yandex", "f3063016-5636-454e-916e-00a756e9b25d");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal("Rock Forever!", result.Name);
        Assert.Equal("The best rock tributes", result.Description);
        Assert.Equal(109, result.TrackCount);
        Assert.Equal(28158, result.Duration);
        Assert.Equal("pl-yandex-f3063016-5636-454e-916e-00a756e9b25d", result.Id);
        Assert.Equal("music-blog", result.CuratorName);
        Assert.Equal("https://get-cover-arts.example.org/very/long/path/filename600x600?121231314", result.CoverUrl);
    }
    
    [Fact]
    public async Task GetPlaylistAsync_WithWrongProvider_ReturnsNull()
    {
        // Act
        var result = await _service.GetPlaylistAsync("deezer", "f3063016-5636-454e-916e-00a756e9b25d");
        
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
            Content = new StringContent("""
            {
              "result": {
                "title": "Rock Forever!",
                "playlistUuid": "f3063016-5636-454e-916e-00a756e9b25d",
                "tracks": [
                  {
                    "originalIndex": 0,
                    "track": {
                      "id": "100001",
                      "title": "Riders on the Storm",
                      "available": true,
                      "durationMs": 434720,
                      "artists": [
                        {
                          "id": 1000010,
                          "name": "The Doors"
                        }
                      ],
                      "albums": [
                        {
                          "id": 1000011,
                          "title": "L.A. Woman"
                        }
                      ]
                    }
                  },
                  {
                    "originalIndex": 2,
                    "track": {
                      "id": "2000002",
                      "title": "I WANNA BE YOUR SLAVE",
                      "contentWarning": "explicit",
                      "available": false,
                      "disclaimers": [
                        "explicit"
                      ],
                      "durationMs": 173370,
                      "artists": [
                        {
                          "id": 2000020,
                          "name": "M\u00e5neskin"
                        }
                      ],
                      "albums": [
                        {
                          "id": 20000022,
                          "title": "Teatro d'ira - Vol. I"
                        }

                      ]
                    }
                  },
                  {
                    "originalIndex": 3,
                    "track": {
                      "id": "300003",
                      "title": "House of Memories",
                      "durationMs": 208700,
                      "artists": [
                        {
                          "id": 3000030,
                          "name": "Panic! At The Disco"
                        }
                      ],
                      "albums": [
                        {
                          "id": 3000033,
                          "title": "Death of a Bachelor"
                        }
                      ]
                    }
                  }
                ]
              }
            }
            """)
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetPlaylistTracksAsync("yandex", "f3063016-5636-454e-916e-00a756e9b25d");
        // Assert
        Assert.NotNull(result);
        // Second track marked as "available": false so it is dropped from search results
        Assert.Equal(2, result.Count);
        
        // First track
        Assert.Equal("Riders on the Storm", result[0].Title);
        Assert.Equal("The Doors", result[0].Artist);
        Assert.Equal("ext-yandex-artist-1000010", result[0].ArtistId);
        Assert.Equal("Rock Forever!", result[0].Album); // Album should be playlist name
        Assert.Equal("pl-yandex-f3063016-5636-454e-916e-00a756e9b25d", result[0].AlbumId);
        Assert.Equal(1, result[0].Track); // Track index starts at 1
        Assert.Equal("yandex", result[0].ExternalProvider);
        Assert.Equal("100001:1000011", result[0].ExternalId);
        
        // Second track
        Assert.Equal("House of Memories", result[1].Title);
        Assert.Equal("Panic! At The Disco", result[1].Artist);
        Assert.Equal("Rock Forever!", result[1].Album); // Album should be playlist name
        Assert.Equal(2, result[1].Track); // Track index increments
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
        var result = await _service.GetPlaylistTracksAsync("yandex", "fdc6fd85-61e8-431b-b773-a789f859aedf");
        
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
            Content = new StringContent("""
            {
              "result": {
                "playlistUuid": "f3063016-5636-454e-916e-00a756e9b25d",
                "tracks": [
                  {
                    "originalIndex": 0,
                    "track": {
                      "id": "100001",
                      "title": "Riders on the Storm",
                      "available": true,
                      "durationMs": 434720,
                      "artists": [
                        {
                          "id": 1000010,
                          "name": "The Doors"
                        }
                      ],
                      "albums": [
                        {
                          "id": 1000011,
                          "title": "L.A. Woman"
                        }
                      ]
                    }
                  }
                ]
              }
            }
            """)
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetPlaylistTracksAsync("yandex", "f3063016-5636-454e-916e-00a756e9b25d");
        
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
            Content = new StringContent("""
            {
                "result": {
                    "tracks": {
                        "results": [
                            {
                                "id": 100,
                                "title": "Track 1",
                                "durationMs": 297910,
                                "artists": [
                                    {
                                        "id": 1000,
                                        "name": "Artist 1"
                                    }
                                ],
                                "albums": [
                                    {
                                        "id": 10000,
                                        "title": "Album 1"
                                    }
                                ],
                                "coverUri": "images.example.org/100/%%"
                            },
                            {
                                "id": 200,
                                "title": "Track 2",
                                "available": false,
                                "durationMs": 297910,
                                "artists": [
                                    {
                                        "id": 2000,
                                        "name": "Artist 2"
                                    }
                                ],
                                "albums": [
                                    {
                                        "id": 20000,
                                        "title": "Album 2"
                                    }
                                ],
                                "coverUri": "images.example.org/200/%%"
                            },
                            {
                                "id": 300,
                                "title": "Track 3",
                                "available": true,
                                "durationMs": 297910,
                                "artists": [
                                    {
                                        "id": 3000,
                                        "name": "Artist 3"
                                    }
                                ],
                                "albums": [
                                    {
                                        "id": 30000,
                                        "title": "Album 3"
                                    }
                                ],
                                "coverUri": "images.example.org/300/%%"
                            }
                        ]
                    }
                }
            }

            """)
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null
                    && req.RequestUri.Query.Contains("page=0")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        var mockResponseSecondPage = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""{"result": {"tracks": {"results": []}}}""")
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null
                    && req.RequestUri.Query.Contains("page=1")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseSecondPage);


        // Act
        var result = await _service.SearchSongsAsync("Track", 20);
        
        // Assert
        Assert.NotNull(result);
        // second result marked as "available": false and shouldn't appear in results
        Assert.Equal(2, result.Count);
        Assert.Equal("Track 1", result[0].Title);
        Assert.Equal("Artist 1", result[0].Artist);
        Assert.Equal("Album 3", result[1].Album);
        Assert.Equal("ext-yandex-album-30000", result[1].AlbumId);
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
            Content = new StringContent("""
            {
                "result": {
                    "albums": {
                        "results": [
                            {
                                "id": 1000,
                                "title": "ABC",
                                "year": 2021,
                                "releaseDate": "2021-09-10T00:00:00+03:00",
                                "trackCount": 1,
                                "artists": [
                                    {
                                        "id": 1,
                                        "name": "Oater"
                                    },
                                    {
                                        "id": 2,
                                        "name": "Daniel Daniel"
                                    },
                                    {
                                        "id": 3,
                                        "name": "Gena"
                                    }
                                ],
                                "labels": [
                                    "CoolLabel"
                                ]
                            }
                        ]
                    }
                }
            }
            """)
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.Query.Contains("page=0")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);

        var mockResponseSecondPage = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""{"result": {"albums": {"results": []}}}""")
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.Query.Contains("page=1")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseSecondPage);


        var mockResponseAlbumDetails = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""
            {
                "result": {
                    "artists": [
                        {
                            "id": 1,
                            "name": "Oater"
                        },
                        {
                            "id": 2,
                            "name": "Daniel Daniel"
                        },
                        {
                            "id": 3,
                            "name": "Gena"
                        }
                    ],
                    "id": 1000,
                    "labels": [
                        "CoolLabel"
                    ],
                    "releaseDate": "2021-09-10T00:00:00+03:00",
                    "title": "ABC",
                    "trackCount": 1,
                    "year": 2021
                }
            }
            """)
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.PathAndQuery.Contains("/albums/1000")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponseAlbumDetails);


        
        // Act
        var result = await _service.SearchAlbumsAsync("ABC", 20);
        
        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ABC", result[0].Title);
        Assert.Equal("Oater", result[0].Artist);
        Assert.Equal(2021, result[0].Year);
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
            Content = new StringContent("""
            {
                "result": [
                    {
                        "id": "88633197",
                        "title": "ABC",
                        "durationMs": 224630,
                        "artists": [
                            {
                                "id": 8624246,
                                "name": "Otica"
                            },
                            {
                                "id": 12756620,
                                "name": "David Diesel"
                            },
                            {
                                "id": 1062258,
                                "name": "Halogen"
                            }
                        ],
                        "albums": [
                            {
                                "id": 17743572,
                                "title": "ATL",
                                "year": 2021,
                                "releaseDate": "2021-09-10T00:00:00+03:00",
                                "coverUri": "get-image.example.org/path/%%%%",
                                "trackCount": 1,
                                "artists": [
                                    {
                                        "id": 8624246,
                                        "name": "Otica"
                                    },
                                    {
                                        "id": 12756620,
                                        "name": "David Diesel"
                                    },
                                    {
                                        "id": 1062258,
                                        "name": "Halogen"
                                    }
                                ],
                                "labels": [
                                    {
                                        "id": 100,
                                        "name": "CoolLabel"
                                    }
                                ],
                                "trackPosition": {
                                    "volume": 1,
                                    "index": 1
                                }
                            }
                        ],
                        "coverUri": "get-image.example.org/path/%%%%",
                        "ogImage": "get-image.example.org/path/%%%%"
                    }
                ]
            }
            """)
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetSongAsync("yandex", "88633197:17743572");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal("ABC", result.Title);
        Assert.Equal("Otica", result.Artist);
        Assert.Equal("ATL", result.Album);
        Assert.Equal("CoolLabel", result.Label);
        Assert.Equal(2021, result.Year);
        Assert.Equal("2021-09-10", result.ReleaseDate);
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
            Content = new StringContent("""
            {
                "result": {
                    "id": 3962240,
                    "title": "La La Land",
                    "year": 2016,
                    "releaseDate": "2016-12-09T00:00:00+03:00",
                    "trackCount": 3,
                    "artists": [
                        {"id": 4080983,"name": "Justin Hurwitz"},
                        {"id": 1035678,"name": "Justin Paul"},
                        {"id": 721193,"name": "Benj Pasek"}
                    ],
                    "labels": [{"id": 2004,"name": "Interscope"}
                    ],
                    "available": true,
                    "volumes": [[{
                        "id": "32504829",
                        "title": "Another Day Of Sun",
                        "available": false,
                        "durationMs": 228170,
                        "artists": [{"id": 4762528,"name": "La La Land Cast"}],
                        "albums": [{"id": 3962240,"title": "La La Land"}]
                    },
                    {
                        "id": "32504830",
                        "title": "Someone In The Crowd",
                        "available": true,
                        "durationMs": 259370,
                        "artists": [{"id": 4762528,"name": "La La Land Cast"}],
                        "albums": [{"id": 3962240,"title": "La La Land"}]
                    },
                    {
                        "id": "32504831",
                        "title": "Mia & Sebastian’s Theme",
                        "durationMs": 96890,
                        "artists": [{"id": 4762528,"name": "La La Land Cast"}],
                        "albums": [{"id": 3962240,"title": "La La Land"}]
                    }]]
                }
            }
            """)
        };
        
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
        
        // Act
        var result = await _service.GetAlbumAsync("yandex", "3962240");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal("La La Land", result.Title);
        Assert.Equal("Justin Hurwitz", result.Artist);
        Assert.Equal(2016, result.Year);
        // first song marked "available": false
        Assert.Equal(2, result.Songs.Count);
        Assert.Equal("Someone In The Crowd", result.Songs[0].Title);
        Assert.Equal("Mia & Sebastian’s Theme", result.Songs[1].Title);
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
