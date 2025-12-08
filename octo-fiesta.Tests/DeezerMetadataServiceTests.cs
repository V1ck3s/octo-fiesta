using octo_fiesta.Services;
using octo_fiesta.Models;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace octo_fiesta.Tests;

public class DeezerMetadataServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly DeezerMetadataService _service;

    public DeezerMetadataServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        
        _service = new DeezerMetadataService(_httpClientFactoryMock.Object);
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
        Assert.Equal("ext-deezer-123456", result[0].Id);
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
        Assert.Equal("ext-deezer-456789", result[0].Id);
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
        Assert.Equal("ext-deezer-789012", result[0].Id);
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
        Assert.Equal("ext-deezer-123456", result.Id);
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
        Assert.Equal("ext-deezer-456789", result.Id);
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
}
