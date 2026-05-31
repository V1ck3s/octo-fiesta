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

    private DeezerMetadataService CreateService(SubsonicSettings settings, DeezerSettings? deezerSettings = null)
    {
        var options = Options.Create(settings);
        var deezerOptions = Options.Create(deezerSettings ?? new DeezerSettings());
        return new DeezerMetadataService(
            _httpClientFactoryMock.Object,
            options,
            deezerOptions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerMetadataService>.Instance);
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
    public async Task SearchAllAsync_WhenArtistMatchesExactly_MergesFullDiscography()
    {
        var albumSearch = new
        {
            data = new[]
            {
                new { id = 100, title = "Master Of Puppets (Remastered)", nb_tracks = 8, artist = new { id = 119, name = "Metallica" } }
            }
        };
        var artistSearch = new
        {
            data = new[]
            {
                new { id = 119, name = "Metallica", nb_album = 68 }
            }
        };
        var emptyTracks = new { data = Array.Empty<object>() };
        var discography = new
        {
            data = new object[]
            {
                new { id = 428391407, title = "72 Seasons", nb_tracks = 12, release_date = "2023-04-14", record_type = "album" },
                new { id = 100, title = "Master Of Puppets (Remastered)", nb_tracks = 8, release_date = "1986-03-03", record_type = "album" }
            }
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                var url = req.RequestUri!.ToString();
                string body;
                if (url.Contains("/search/track")) body = JsonSerializer.Serialize(emptyTracks);
                else if (url.Contains("/search/album")) body = JsonSerializer.Serialize(albumSearch);
                else if (url.Contains("/search/artist")) body = JsonSerializer.Serialize(artistSearch);
                else if (url.Contains("/artist/119/albums")) body = JsonSerializer.Serialize(discography);
                else throw new InvalidOperationException($"Unexpected URL: {url}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            });

        var result = await _service.SearchAllAsync("Metallica", 5, 5, 5);

        Assert.Contains(result.Albums, a => a.ExternalId == "428391407" && a.Year == 2023);
        Assert.Equal(2, result.Albums.Count);
        Assert.Single(result.Albums, a => a.ExternalId == "100");
        var discographyAlbum = result.Albums.Single(a => a.ExternalId == "428391407");
        Assert.Equal("Metallica", discographyAlbum.Artist);
        Assert.Equal("ext-deezer-artist-119", discographyAlbum.ArtistId);
    }

    [Fact]
    public async Task SearchAllAsync_WhenNoArtistMatchesExactly_DoesNotFetchDiscography()
    {
        var albumSearch = new { data = new[] { new { id = 100, title = "Some Album", nb_tracks = 8, artist = new { id = 999, name = "Metallica Tribute Band" } } } };
        var artistSearch = new { data = new[] { new { id = 999, name = "Metallica Tribute Band", nb_album = 0 } } };
        var emptyTracks = new { data = Array.Empty<object>() };
        var discographyCallCount = 0;

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                var url = req.RequestUri!.ToString();
                string body;
                if (url.Contains("/search/track")) body = JsonSerializer.Serialize(emptyTracks);
                else if (url.Contains("/search/album")) body = JsonSerializer.Serialize(albumSearch);
                else if (url.Contains("/search/artist")) body = JsonSerializer.Serialize(artistSearch);
                else if (url.Contains("/artist/") && url.Contains("/albums")) { discographyCallCount++; body = "{\"data\":[]}"; }
                else throw new InvalidOperationException($"Unexpected URL: {url}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            });

        var result = await _service.SearchAllAsync("Metallica", 5, 5, 5);

        Assert.Single(result.Albums);
        Assert.Equal(0, discographyCallCount);
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
            tracklist = "https://api.deezer.com/album/456789/tracks"
        };

        var deezerTracklistResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 111,
                    title = "Track 1",
                    duration = 180,
                    track_position = 1,
                    artist = new { id = 123, name = "Test Artist" }
                },
                new
                {
                    id = 222,
                    title = "Track 2",
                    duration = 200,
                    track_position = 2,
                    artist = new { id = 123, name = "Test Artist" }
                }
            }
        };

        SetupSequentialHttpResponses(
            JsonSerializer.Serialize(deezerResponse),
            JsonSerializer.Serialize(deezerTracklistResponse));

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
        Assert.All(result.Songs, s => Assert.Equal("https://example.com/album.jpg", s.CoverArtUrl));
    }

    [Fact]
    public async Task GetAlbumAsync_WithDeezerProvider_ReturnsAllTracksWithPaginatedTracklist()
    {
        // Arrange
        var deezerResponse = new
        {
            id = 456789,
            title = "Test Album",
            nb_tracks = 4,
            release_date = "2023-05-20",
            cover_medium = "https://example.com/album.jpg",
            artist = new { id = 123, name = "Test Artist" },
            tracklist = "https://api.deezer.com/album/456789/tracks"
        };

        var tracklistPage1 = new
        {
            data = new[]
            {
                new { id = 111, title = "Track 1", duration = 180, track_position = 1, artist = new { id = 123, name = "Test Artist" } },
                new { id = 222, title = "Track 2", duration = 200, track_position = 2, artist = new { id = 123, name = "Test Artist" } }
            },
            next = "https://api.deezer.com/album/456789/tracks?limit=1000&index=1000"
        };

        var tracklistPage2 = new
        {
            data = new[]
            {
                new { id = 333, title = "Track 3", duration = 210, track_position = 3, artist = new { id = 123, name = "Test Artist" } },
                new { id = 444, title = "Track 4", duration = 220, track_position = 4, artist = new { id = 123, name = "Test Artist" } }
            }
        };

        SetupSequentialHttpResponses(
            JsonSerializer.Serialize(deezerResponse),
            JsonSerializer.Serialize(tracklistPage1),
            JsonSerializer.Serialize(tracklistPage2));

        // Act
        var result = await _service.GetAlbumAsync("deezer", "456789");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Songs.Count);
        Assert.Equal("Track 1", result.Songs[0].Title);
        Assert.Equal("Track 2", result.Songs[1].Title);
        Assert.Equal("Track 3", result.Songs[2].Title);
        Assert.Equal("Track 4", result.Songs[3].Title);
        Assert.All(result.Songs, s => Assert.Equal("https://example.com/album.jpg", s.CoverArtUrl));
    }

    [Fact]
    public async Task GetAlbumAsync_WithDeezerProvider_WithoutTracklistUrlFallsBackToTracksData()
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

    private void SetupSequentialHttpResponses(params string[] contents)
    {
        var seq = _httpMessageHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var content in contents)
        {
            seq = seq.ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(content)
            });
        }
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
            tracklist = "https://api.deezer.com/album/456789/tracks"
        };

        var deezerTracklistResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 111,
                    title = "Explicit Track",
                    duration = 180,
                    explicit_content_lyrics = 1,
                    artist = new { id = 123, name = "Test Artist" }
                },
                new
                {
                    id = 222,
                    title = "Clean Version Track",
                    duration = 200,
                    explicit_content_lyrics = 3, // Should be excluded
                    artist = new { id = 123, name = "Test Artist" }
                },
                new
                {
                    id = 333,
                    title = "Naturally Clean Track",
                    duration = 220,
                    explicit_content_lyrics = 0,
                    artist = new { id = 123, name = "Test Artist" }
                }
            }
        };

        SetupSequentialHttpResponses(
            JsonSerializer.Serialize(deezerResponse),
            JsonSerializer.Serialize(deezerTracklistResponse));

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
                        track_position = 11,
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
                        track_position = 42,
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
        Assert.Equal(1, result[0].Track);
        Assert.Equal(2, result[1].Track);
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

    #region Private gateway search (issue #232)

    // Dispatches gw-light getUserData (token) + deezer.pageSearch, and falls through
    // to a public-API responder for anything else.
    private void SetupGatewaySearch(string pageSearchJson, string? userDataJson = null, Func<string, string?>? publicResponder = null)
    {
        userDataJson ??= JsonSerializer.Serialize(new { error = Array.Empty<object>(), results = new { checkForm = "test-token" } });

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                var url = req.RequestUri!.ToString();
                string body;
                if (url.Contains("method=deezer.getUserData")) body = userDataJson;
                else if (url.Contains("method=deezer.pageSearch")) body = pageSearchJson;
                else body = publicResponder?.Invoke(url) ?? "{\"data\":[]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            });
    }

    private static string BuildPageSearchJson()
    {
        var payload = new
        {
            error = Array.Empty<object>(),
            results = new
            {
                ARTIST = new { count = 1, data = new[] { new { ART_ID = "119", ART_NAME = "Metallica", ART_PICTURE = "artmd5" } } },
                ALBUM = new
                {
                    count = 2,
                    data = new[]
                    {
                        new { ALB_ID = "100", ALB_TITLE = "Master Of Puppets (Remastered)", ART_ID = "119", ART_NAME = "Metallica", ALB_PICTURE = "albmd5a", NUMBER_TRACK = "8", ORIGINAL_RELEASE_DATE = "1986-03-03" },
                        new { ALB_ID = "428391407", ALB_TITLE = "72 Seasons", ART_ID = "119", ART_NAME = "Metallica", ALB_PICTURE = "albmd5b", NUMBER_TRACK = "12", ORIGINAL_RELEASE_DATE = "2023-04-14" }
                    }
                },
                TRACK = new
                {
                    count = 1,
                    data = new[]
                    {
                        new
                        {
                            SNG_ID = "555", SNG_TITLE = "Lux Æterna", ALB_ID = "428391407", ALB_TITLE = "72 Seasons",
                            ALB_PICTURE = "albmd5b", ART_ID = "119", ART_NAME = "Metallica", DURATION = "228",
                            TRACK_NUMBER = "1", DISK_NUMBER = "1", ISRC = "USXXX2300001", EXPLICIT_LYRICS = "0",
                            ARTISTS = new[] { new { ART_ID = "119", ART_NAME = "Metallica", ART_PICTURE = "artmd5" } }
                        }
                    }
                }
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    [Fact]
    public async Task SearchAllAsync_UsesGateway_SurfacesLatestRelease()
    {
        // Public API would be IP-geo-filtered (no 72 Seasons); the gateway returns it.
        SetupGatewaySearch(BuildPageSearchJson(), publicResponder: _ => "{\"data\":[]}");
        _service = CreateService(new SubsonicSettings { ExplicitFilter = ExplicitFilter.All }, new DeezerSettings { Arl = "fake-arl" });

        var result = await _service.SearchAllAsync("Metallica", 20, 20, 20);

        Assert.Contains(result.Albums, a => a.ExternalId == "428391407" && a.Title == "72 Seasons" && a.Year == 2023);
        var latest = result.Albums.Single(a => a.ExternalId == "428391407");
        Assert.Equal("Metallica", latest.Artist);
        Assert.Equal("ext-deezer-artist-119", latest.ArtistId);
        Assert.Equal(12, latest.SongCount);
        Assert.StartsWith("https://e-cdns-images.dzcdn.net/images/cover/albmd5b/", latest.CoverArtUrl);

        Assert.Single(result.Artists, a => a.ExternalId == "119" && a.Name == "Metallica");
        var song = Assert.Single(result.Songs);
        Assert.Equal("555", song.ExternalId);
        Assert.Equal("Lux Æterna", song.Title);
        Assert.Equal(228, song.Duration);
        Assert.Equal("ext-deezer-album-428391407", song.AlbumId);
        Assert.Equal("USXXX2300001", song.Isrc);
        Assert.Single(song.Artists, a => a.ExternalId == "119");
    }

    [Fact]
    public async Task SearchAllAsync_GatewayRespectsPerSectionLimits()
    {
        SetupGatewaySearch(BuildPageSearchJson());
        _service = CreateService(new SubsonicSettings { ExplicitFilter = ExplicitFilter.All });

        var result = await _service.SearchAllAsync("Metallica", songLimit: 0, albumLimit: 1, artistLimit: 20);

        Assert.Empty(result.Songs);          // songLimit 0
        Assert.Single(result.Albums);        // albumLimit 1
        Assert.Single(result.Artists);
    }

    [Fact]
    public async Task SearchAllAsync_FallsBackToPublicApi_WhenGatewayTokenUnavailable()
    {
        // getUserData without checkForm -> no token -> fall back to the public API.
        var noToken = JsonSerializer.Serialize(new { error = Array.Empty<object>(), results = new { } });
        var publicAlbum = JsonSerializer.Serialize(new { data = new[] { new { id = 999, title = "Public Album", nb_tracks = 10, release_date = "2020-01-01", artist = new { id = 7, name = "Pub Artist" } } } });

        SetupGatewaySearch("{}", userDataJson: noToken, publicResponder: url => url.Contains("/search/album") ? publicAlbum : "{\"data\":[]}");
        _service = CreateService(new SubsonicSettings { ExplicitFilter = ExplicitFilter.All });

        var result = await _service.SearchAllAsync("anything", 20, 20, 20);

        Assert.Single(result.Albums, a => a.ExternalId == "999" && a.Title == "Public Album");
    }

    #endregion
}
