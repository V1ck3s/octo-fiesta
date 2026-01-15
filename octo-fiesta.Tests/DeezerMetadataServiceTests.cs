using octo_fiesta.Services.Deezer;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using Moq;
using Moq.Protected;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace octo_fiesta.Tests;

public class DeezerMetadataServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly SubsonicSettings _settings;
    private DeezerMetadataService _service;

    public DeezerMetadataServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        
        _settings = new SubsonicSettings { ExplicitFilter = ExplicitFilter.ExplicitOnly };
        _service = CreateService(_settings);
    }

    private DeezerMetadataService CreateService(SubsonicSettings settings)
    {
        var options = Options.Create(settings);
        return new DeezerMetadataService(_httpClientFactoryMock.Object, options);
    }

    [Fact]
    public async Task SearchSongsAsync_ReturnsListOfSongs()
    {
        // Arrange
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 123456,
                    title = "Test Song",
                    duration = 180,
                    track_position = 1,
                    artist = new { id = 789, name = "Test Artist" },
                    album = new { id = 456, title = "Test Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchSongsAsync("test query", 20);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ext-deezer-song-123456", result[0].Id);
        Assert.Equal("Test Song", result[0].Title);
        Assert.Equal("Test Artist", result[0].Artist);
        Assert.Equal("Test Album", result[0].Album);
        Assert.Equal(180, result[0].Duration);
        Assert.False(result[0].IsLocal);
        Assert.Equal("deezer", result[0].ExternalProvider);
    }

    [Fact]
    public async Task SearchAlbumsAsync_ReturnsListOfAlbums()
    {
        // Arrange
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 456789,
                    title = "Test Album",
                    nb_tracks = 12,
                    release_date = "2023-01-15",
                    cover_medium = "https://example.com/album.jpg",
                    artist = new { id = 123, name = "Test Artist" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchAlbumsAsync("test album", 20);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ext-deezer-album-456789", result[0].Id);
        Assert.Equal("Test Album", result[0].Title);
        Assert.Equal("Test Artist", result[0].Artist);
        Assert.Equal(12, result[0].SongCount);
        Assert.Equal(2023, result[0].Year);
        Assert.False(result[0].IsLocal);
    }

    [Fact]
    public async Task SearchArtistsAsync_ReturnsListOfArtists()
    {
        // Arrange
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 789012,
                    name = "Test Artist",
                    nb_album = 5,
                    picture_medium = "https://example.com/artist.jpg"
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchArtistsAsync("test artist", 20);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ext-deezer-artist-789012", result[0].Id);
        Assert.Equal("Test Artist", result[0].Name);
        Assert.Equal(5, result[0].AlbumCount);
        Assert.False(result[0].IsLocal);
    }

    [Fact]
    public async Task SearchAllAsync_ReturnsAllTypes()
    {
        // This test would need multiple HTTP calls mocked, simplified for now
        var emptyResponse = JsonSerializer.Serialize(new { data = Array.Empty<object>() });
        SetupHttpResponse(emptyResponse);

        // Act
        var result = await _service.SearchAllAsync("test");

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Songs);
        Assert.NotNull(result.Albums);
        Assert.NotNull(result.Artists);
    }

    [Fact]
    public async Task GetSongAsync_WithDeezerProvider_ReturnsSong()
    {
        // Arrange
        var deezerResponse = new
        {
            id = 123456,
            title = "Test Song",
            duration = 200,
            track_position = 3,
            artist = new { id = 789, name = "Test Artist" },
            album = new { id = 456, title = "Test Album", cover_medium = "https://example.com/cover.jpg" }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.GetSongAsync("deezer", "123456");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("ext-deezer-song-123456", result.Id);
        Assert.Equal("Test Song", result.Title);
    }

    [Fact]
    public async Task GetSongAsync_WithNonDeezerProvider_ReturnsNull()
    {
        // Act
        var result = await _service.GetSongAsync("spotify", "123456");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SearchSongsAsync_WithEmptyResponse_ReturnsEmptyList()
    {
        // Arrange
        SetupHttpResponse(JsonSerializer.Serialize(new { data = Array.Empty<object>() }));

        // Act
        var result = await _service.SearchSongsAsync("nonexistent", 20);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchSongsAsync_WithHttpError_ReturnsEmptyList()
    {
        // Arrange
        SetupHttpResponse("Error", HttpStatusCode.InternalServerError);

        // Act
        var result = await _service.SearchSongsAsync("test", 20);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAlbumAsync_WithDeezerProvider_ReturnsAlbumWithTracks()
    {
        // Arrange
        var deezerResponse = new
        {
            id = 456789,
            title = "Test Album",
            nb_tracks = 2,
            release_date = "2023-05-20",
            cover_medium = "https://example.com/album.jpg",
            artist = new { id = 123, name = "Test Artist" },
            tracks = new
            {
                data = new[]
                {
                    new
                    {
                        id = 111,
                        title = "Track 1",
                        duration = 180,
                        track_position = 1,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    },
                    new
                    {
                        id = 222,
                        title = "Track 2",
                        duration = 200,
                        track_position = 2,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.GetAlbumAsync("deezer", "456789");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("ext-deezer-album-456789", result.Id);
        Assert.Equal("Test Album", result.Title);
        Assert.Equal("Test Artist", result.Artist);
        Assert.Equal(2, result.Songs.Count);
        Assert.Equal("Track 1", result.Songs[0].Title);
        Assert.Equal("Track 2", result.Songs[1].Title);
    }

    [Fact]
    public async Task GetAlbumAsync_WithNonDeezerProvider_ReturnsNull()
    {
        // Act
        var result = await _service.GetAlbumAsync("spotify", "123456");

        // Assert
        Assert.Null(result);
    }

    private void SetupHttpResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
    }

    #region Explicit Filter Tests

    [Fact]
    public async Task SearchSongsAsync_ExplicitOnlyFilter_ExcludesCleanVersions()
    {
        // Arrange
        _service = CreateService(new SubsonicSettings { ExplicitFilter = ExplicitFilter.ExplicitOnly });
        
        var deezerResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 1,
                    title = "Explicit Original",
                    duration = 180,
                    explicit_content_lyrics = 1, // Explicit
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                },
                new
                {
                    id = 2,
                    title = "Clean Version",
                    duration = 180,
                    explicit_content_lyrics = 3, // Clean/edited - should be excluded
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                },
                new
                {
                    id = 3,
                    title = "Naturally Clean",
                    duration = 180,
                    explicit_content_lyrics = 0, // Naturally clean - should be included
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchSongsAsync("test", 20);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Title == "Explicit Original");
        Assert.Contains(result, s => s.Title == "Naturally Clean");
        Assert.DoesNotContain(result, s => s.Title == "Clean Version");
    }

    [Fact]
    public async Task SearchSongsAsync_CleanOnlyFilter_ExcludesExplicitContent()
    {
        // Arrange
        _service = CreateService(new SubsonicSettings { ExplicitFilter = ExplicitFilter.CleanOnly });
        
        var deezerResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 1,
                    title = "Explicit Original",
                    duration = 180,
                    explicit_content_lyrics = 1, // Explicit - should be excluded
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                },
                new
                {
                    id = 2,
                    title = "Clean Version",
                    duration = 180,
                    explicit_content_lyrics = 3, // Clean/edited - should be included
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                },
                new
                {
                    id = 3,
                    title = "Naturally Clean",
                    duration = 180,
                    explicit_content_lyrics = 0, // Naturally clean - should be included
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchSongsAsync("test", 20);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Title == "Clean Version");
        Assert.Contains(result, s => s.Title == "Naturally Clean");
        Assert.DoesNotContain(result, s => s.Title == "Explicit Original");
    }

    [Fact]
    public async Task SearchSongsAsync_AllFilter_IncludesEverything()
    {
        // Arrange
        _service = CreateService(new SubsonicSettings { ExplicitFilter = ExplicitFilter.All });
        
        var deezerResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 1,
                    title = "Explicit Original",
                    duration = 180,
                    explicit_content_lyrics = 1,
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                },
                new
                {
                    id = 2,
                    title = "Clean Version",
                    duration = 180,
                    explicit_content_lyrics = 3,
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                },
                new
                {
                    id = 3,
                    title = "Naturally Clean",
                    duration = 180,
                    explicit_content_lyrics = 0,
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchSongsAsync("test", 20);

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task SearchSongsAsync_ExplicitOnlyFilter_IncludesTracksWithNoExplicitInfo()
    {
        // Arrange
        _service = CreateService(new SubsonicSettings { ExplicitFilter = ExplicitFilter.ExplicitOnly });
        
        var deezerResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 1,
                    title = "No Explicit Info",
                    duration = 180,
                    // No explicit_content_lyrics field
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchSongsAsync("test", 20);

        // Assert
        Assert.Single(result);
        Assert.Equal("No Explicit Info", result[0].Title);
    }

    [Fact]
    public async Task GetAlbumAsync_ExplicitOnlyFilter_FiltersAlbumTracks()
    {
        // Arrange
        _service = CreateService(new SubsonicSettings { ExplicitFilter = ExplicitFilter.ExplicitOnly });
        
        var deezerResponse = new
        {
            id = 456789,
            title = "Test Album",
            nb_tracks = 3,
            release_date = "2023-05-20",
            cover_medium = "https://example.com/album.jpg",
            artist = new { id = 123, name = "Test Artist" },
            tracks = new
            {
                data = new object[]
                {
                    new
                    {
                        id = 111,
                        title = "Explicit Track",
                        duration = 180,
                        explicit_content_lyrics = 1,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    },
                    new
                    {
                        id = 222,
                        title = "Clean Version Track",
                        duration = 200,
                        explicit_content_lyrics = 3, // Should be excluded
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    },
                    new
                    {
                        id = 333,
                        title = "Naturally Clean Track",
                        duration = 220,
                        explicit_content_lyrics = 0,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.GetAlbumAsync("deezer", "456789");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Songs.Count);
        Assert.Contains(result.Songs, s => s.Title == "Explicit Track");
        Assert.Contains(result.Songs, s => s.Title == "Naturally Clean Track");
        Assert.DoesNotContain(result.Songs, s => s.Title == "Clean Version Track");
    }

    [Fact]
    public async Task SearchSongsAsync_ParsesExplicitContentLyrics()
    {
        // Arrange
        var deezerResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 1,
                    title = "Test Track",
                    duration = 180,
                    explicit_content_lyrics = 1,
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchSongsAsync("test", 20);

        // Assert
        Assert.Single(result);
        Assert.Equal(1, result[0].ExplicitContentLyrics);
    }

    #endregion

    #region Playlist Tests

    [Fact]
    public async Task SearchPlaylistsAsync_ReturnsListOfPlaylists()
    {
        // Arrange
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 12345,
                    title = "Chill Vibes",
                    nb_tracks = 50,
                    picture_medium = "https://example.com/playlist1.jpg",
                    user = new { name = "Test User" }
                },
                new
                {
                    id = 67890,
                    title = "Workout Mix",
                    nb_tracks = 30,
                    picture_medium = "https://example.com/playlist2.jpg",
                    user = new { name = "Gym Buddy" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchPlaylistsAsync("chill");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Chill Vibes", result[0].Name);
        Assert.Equal(50, result[0].TrackCount);
        Assert.Equal("pl-deezer-12345", result[0].Id);
    }

    [Fact]
    public async Task SearchPlaylistsAsync_WithLimit_RespectsLimit()
    {
        // Arrange
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 12345,
                    title = "Playlist 1",
                    nb_tracks = 10,
                    picture_medium = "https://example.com/p1.jpg",
                    user = new { name = "User 1" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchPlaylistsAsync("test", 1);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task SearchPlaylistsAsync_WithEmptyResults_ReturnsEmptyList()
    {
        // Arrange
        var deezerResponse = new
        {
            data = new object[] { }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.SearchPlaylistsAsync("nonexistent");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPlaylistAsync_WithValidId_ReturnsPlaylist()
    {
        // Arrange
        var deezerResponse = new
        {
            id = 12345,
            title = "Best Of Jazz",
            description = "The best jazz tracks",
            nb_tracks = 100,
            picture_medium = "https://example.com/jazz.jpg",
            user = new { name = "Jazz Lover" }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.GetPlaylistAsync("deezer", "12345");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Best Of Jazz", result.Name);
        Assert.Equal(100, result.TrackCount);
        Assert.Equal("pl-deezer-12345", result.Id);
    }

    [Fact]
    public async Task GetPlaylistAsync_WithWrongProvider_ReturnsNull()
    {
        // Act
        var result = await _service.GetPlaylistAsync("qobuz", "12345");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_ReturnsListOfSongs()
    {
        // Arrange
        var deezerResponse = new
        {
            tracks = new
            {
                data = new[]
                {
                    new
                    {
                        id = 111,
                        title = "Track 1",
                        duration = 200,
                        track_position = 1,
                        disk_number = 1,
                        artist = new
                        {
                            id = 999,
                            name = "Artist A"
                        },
                        album = new
                        {
                            id = 888,
                            title = "Album X",
                            release_date = "2020-01-15",
                            cover_medium = "https://example.com/cover.jpg"
                        }
                    },
                    new
                    {
                        id = 222,
                        title = "Track 2",
                        duration = 180,
                        track_position = 2,
                        disk_number = 1,
                        artist = new
                        {
                            id = 777,
                            name = "Artist B"
                        },
                        album = new
                        {
                            id = 666,
                            title = "Album Y",
                            release_date = "2021-05-20",
                            cover_medium = "https://example.com/cover2.jpg"
                        }
                    }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.GetPlaylistTracksAsync("deezer", "12345");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Track 1", result[0].Title);
        Assert.Equal("Artist A", result[0].Artist);
        Assert.Equal("ext-deezer-song-111", result[0].Id);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_WithWrongProvider_ReturnsEmptyList()
    {
        // Act
        var result = await _service.GetPlaylistTracksAsync("qobuz", "12345");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_WithEmptyPlaylist_ReturnsEmptyList()
    {
        // Arrange
        var deezerResponse = new
        {
            tracks = new
            {
                data = new object[] { }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        // Act
        var result = await _service.GetPlaylistTracksAsync("deezer", "12345");

        // Assert
        Assert.Empty(result);
    }

    #endregion
}
