using octo_fiesta.Services.Local;
using octo_fiesta.Services;
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

namespace octo_fiesta.Tests;

public class LocalLibraryServiceTests : IDisposable
{
    private readonly LocalLibraryService _service;
    private readonly string _testDownloadPath;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly Mock<IMusicMetadataService> _mockMetadataService;

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
        _mockHandler = new Mock<HttpMessageHandler>();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":true}}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"scanStatus\":{\"scanning\":false,\"count\":100}}}")
                };
            });
        
        var httpClient = new HttpClient(_mockHandler.Object);
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);
        _mockMetadataService = new Mock<IMusicMetadataService>();

        var subsonicSettings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });
        var mockLogger = new Mock<ILogger<LocalLibraryService>>();

        _service = new LocalLibraryService(configuration, _mockHttpClientFactory.Object, _mockMetadataService.Object, subsonicSettings, mockLogger.Object);
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
    public async Task TriggerLibraryScanAsync_WithoutCredentials_ReturnsFalse()
    {
        // Act - no credentials set, admin check fails
        var result = await _service.TriggerLibraryScanAsync();

        // Assert
        Assert.False(result);
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

    [Fact]
    public async Task WaitForLocalIdAfterScanAsync_WhenScanCompletes_ResolvesId()
    {
        var song = new Song
        {
            Title = "Scanned Song",
            Artist = "Scan Artist",
            Album = "Scan Album",
            ExternalProvider = "deezer",
            ExternalId = "scan-id"
        };
        var localPath = Path.Combine(_testDownloadPath, "scan-song.mp3");
        await File.WriteAllTextAsync(localPath, "fake audio content");
        await _service.RegisterDownloadedSongAsync(song, localPath);
        _service.SetSubsonicCredentials(new Dictionary<string, string>
        {
            ["u"] = "admin",
            ["t"] = "token",
            ["s"] = "salt",
            ["v"] = "1.16.1",
            ["c"] = "tests"
        });

        var statusCalls = 0;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":true}}}")
                    };
                }

                if (url.Contains("startScan"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                    };
                }

                if (url.Contains("getScanStatus"))
                {
                    statusCalls++;
                    var scanning = statusCalls == 1 ? "true" : "false";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"subsonic-response\":{{\"status\":\"ok\",\"scanStatus\":{{\"scanning\":{scanning},\"count\":100}}}}}}")
                    };
                }

                if (url.Contains("search3"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"searchResult3\":{\"song\":[{\"id\":\"555\",\"title\":\"Scanned Song\",\"artist\":\"Scan Artist\",\"album\":\"Scan Album\"}]}}}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        var result = await _service.WaitForLocalIdAfterScanAsync("deezer", "scan-id");

        Assert.Equal("555", result);
    }

    [Fact]
    public async Task GetLocalIdForExternalSongAsync_WithoutMapping_ResolvesViaMetadataAndSearch3()
    {
        _mockMetadataService
            .Setup(x => x.GetSongAsync("deezer", "fallback-id"))
            .ReturnsAsync(new Song
            {
                Title = "Fallback Song",
                Artist = "Fallback Artist",
                Album = "Fallback Album",
                ExternalProvider = "deezer",
                ExternalId = "fallback-id"
            });

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("search3"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"searchResult3\":{\"song\":[{\"id\":\"777\",\"title\":\"Fallback Song\",\"artist\":\"Fallback Artist\",\"album\":\"Fallback Album\"}]}}}")
                    };
                }

                if (url.Contains("getUser"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":true}}}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        var result = await _service.GetLocalIdForExternalSongAsync("deezer", "fallback-id");

        Assert.Equal("777", result);
    }

    [Theory]
    [InlineData("ext-deezer-123", true, "deezer", "123")]
    [InlineData("ext-spotify-abc123", true, "spotify", "abc123")]
    [InlineData("ext-tidal-999-888", true, "tidal", "999-888")]
    [InlineData("ext-deezer-song-123456", true, "deezer", "123456")]  // New format - extracts numeric ID
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

    [Theory]
    [InlineData("ext-deezer-song-123456", true, "deezer", "song", "123456")]
    [InlineData("ext-deezer-album-789012", true, "deezer", "album", "789012")]
    [InlineData("ext-deezer-artist-259", true, "deezer", "artist", "259")]
    [InlineData("ext-spotify-song-abc123", true, "spotify", "song", "abc123")]
    [InlineData("ext-deezer-123", true, "deezer", "song", "123")]  // Legacy format defaults to song
    [InlineData("ext-tidal-999", true, "tidal", "song", "999")]    // Legacy format defaults to song
    [InlineData("123456", false, null, null, null)]
    [InlineData("", false, null, null, null)]
    [InlineData("ext-", false, null, null, null)]
    [InlineData("ext-deezer", false, null, null, null)]
    public void ParseExternalId_VariousInputs_ReturnsExpected(string id, bool expectedIsExternal, string? expectedProvider, string? expectedType, string? expectedExternalId)
    {
        // Act
        var (isExternal, provider, type, externalId) = _service.ParseExternalId(id);

        // Assert
        Assert.Equal(expectedIsExternal, isExternal);
        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedExternalId, externalId);
    }

    [Fact]
    public void SetSubsonicCredentials_StoresCredentialsOnFirstCall()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["u"] = "testuser",
            ["t"] = "token123",
            ["s"] = "salt456",
            ["v"] = "1.16.1",
            ["c"] = "aonsoku"
        };

        // Act - should not throw
        _service.SetSubsonicCredentials(parameters);

        // Assert - credentials are stored (verified indirectly through scan URL)
    }

    [Fact]
    public async Task TriggerLibraryScanAsync_WithCredentials_IncludesAuthInRequest()
    {
        // Arrange
        var capturedUris = new List<Uri?>();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUris.Add(req.RequestUri))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":true}}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        _service.SetSubsonicCredentials(new Dictionary<string, string>
        {
            ["u"] = "admin",
            ["t"] = "abc123",
            ["s"] = "xyz789",
            ["v"] = "1.16.1",
            ["c"] = "feishin"
        });

        // Act
        await _service.TriggerLibraryScanAsync();

        // Assert - should have made 2 requests: getUser + startScan
        var scanUri = capturedUris.FirstOrDefault(u => u?.ToString().Contains("startScan") == true);
        Assert.NotNull(scanUri);
        var query = scanUri!.Query;
        Assert.Contains("u=admin", query);
        Assert.Contains("t=abc123", query);
        Assert.Contains("s=xyz789", query);
        Assert.Contains("v=1.16.1", query);
        Assert.Contains("c=feishin", query);
    }

    [Fact]
    public async Task TriggerLibraryScanAsync_WithoutCredentials_DoesNotSendRequest()
    {
        // Act - no credentials set
        await _service.TriggerLibraryScanAsync();

        // Assert - no HTTP request should be made
        _mockHandler.Protected()
            .Verify("SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SetSubsonicCredentials_IgnoresSecondCall()
    {
        // Arrange
        var firstParams = new Dictionary<string, string>
        {
            ["u"] = "firstuser",
            ["t"] = "token1",
            ["s"] = "salt1",
            ["v"] = "1.16.1",
            ["c"] = "client1"
        };
        var secondParams = new Dictionary<string, string>
        {
            ["u"] = "seconduser",
            ["t"] = "token2",
            ["s"] = "salt2",
            ["v"] = "1.16.1",
            ["c"] = "client2"
        };

        // Act
        _service.SetSubsonicCredentials(firstParams);
        _service.SetSubsonicCredentials(secondParams);

        // Assert - verified indirectly: scan should use first user's credentials
        var capturedUris = new List<Uri?>();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUris.Add(req.RequestUri))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":true}}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        await _service.TriggerLibraryScanAsync();

        var scanUri = capturedUris.FirstOrDefault(u => u?.ToString().Contains("startScan") == true);
        Assert.NotNull(scanUri);
        Assert.Contains("u=firstuser", scanUri!.Query);
        Assert.DoesNotContain("seconduser", scanUri.Query);
    }

    [Fact]
    public async Task SetSubsonicCredentials_WithoutUsername_DoesNotStore()
    {
        // Arrange - params without 'u'
        var parameters = new Dictionary<string, string>
        {
            ["t"] = "token123",
            ["s"] = "salt456"
        };

        // Act
        _service.SetSubsonicCredentials(parameters);

        // Assert - a second call with valid params should be accepted (first was ignored)
        var validParams = new Dictionary<string, string>
        {
            ["u"] = "realuser",
            ["t"] = "realtoken",
            ["s"] = "realsalt",
            ["v"] = "1.16.1",
            ["c"] = "client"
        };
        _service.SetSubsonicCredentials(validParams);

        var capturedUris = new List<Uri?>();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUris.Add(req.RequestUri))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":true}}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        await _service.TriggerLibraryScanAsync();

        var scanUri = capturedUris.FirstOrDefault(u => u?.ToString().Contains("startScan") == true);
        Assert.NotNull(scanUri);
        Assert.Contains("u=realuser", scanUri!.Query);
    }

    // --- FindPlaylistsContainingSongAsync tests ---

    [Fact]
    public async Task FindPlaylistsContainingSongAsync_WithoutCredentials_ReturnsEmptyList()
    {
        var result = await _service.FindPlaylistsContainingSongAsync("some-id");
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindPlaylistsContainingSongAsync_SongInOnePlaylist_ReturnsThatPlaylist()
    {
        SetupCredentials();
        SetupPlaylistResponses(new[]
        {
            ("pl-1", "My Playlist", new[] { "song-a", "song-b", "song-c" })
        });

        var result = await _service.FindPlaylistsContainingSongAsync("song-b");

        Assert.Single(result);
        Assert.Equal("pl-1", result[0].PlaylistId);
        Assert.Equal("My Playlist", result[0].PlaylistName);
    }

    [Fact]
    public async Task FindPlaylistsContainingSongAsync_SongInMultiplePlaylists_ReturnsAll()
    {
        SetupCredentials();
        SetupPlaylistResponses(new[]
        {
            ("pl-1", "Playlist One", new[] { "song-a", "song-x" }),
            ("pl-2", "Playlist Two", new[] { "song-x", "song-b" }),
            ("pl-3", "Playlist Three", new[] { "song-c" })
        });

        var result = await _service.FindPlaylistsContainingSongAsync("song-x");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.PlaylistId == "pl-1");
        Assert.Contains(result, p => p.PlaylistId == "pl-2");
    }

    [Fact]
    public async Task FindPlaylistsContainingSongAsync_SongNotInAnyPlaylist_ReturnsEmptyList()
    {
        SetupCredentials();
        SetupPlaylistResponses(new[]
        {
            ("pl-1", "Playlist", new[] { "song-a", "song-b" })
        });

        var result = await _service.FindPlaylistsContainingSongAsync("nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindPlaylistsContainingSongAsync_GetPlaylistsFails_ReturnsEmptyList()
    {
        SetupCredentials();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getPlaylists"))
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        var result = await _service.FindPlaylistsContainingSongAsync("song-a");

        Assert.Empty(result);
    }

    // --- MigratePlaylistEntriesAsync tests ---

    [Fact]
    public async Task MigratePlaylistEntriesAsync_RebuildsPlaylistWithCorrectOrder()
    {
        SetupCredentials();
        var capturedUris = new List<Uri?>();

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUris.Add(req.RequestUri))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getPlaylist") && !url.Contains("getPlaylists"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"subsonic-response\":{\"playlist\":{\"entry\":[" +
                            "{\"id\":\"song-a\",\"title\":\"A\"}," +
                            "{\"id\":\"old-id\",\"title\":\"B\"}," +
                            "{\"id\":\"song-c\",\"title\":\"C\"}" +
                            "]}}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        await _service.MigratePlaylistEntriesAsync("old-id", "new-id",
            new List<(string, string)> { ("pl-1", "Test Playlist") });

        var updateUri = capturedUris.FirstOrDefault(u => u?.ToString().Contains("updatePlaylist") == true);
        Assert.NotNull(updateUri);
        var query = updateUri!.ToString();

        // Verify all 3 entries removed by index
        Assert.Contains("songIndexToRemove=0", query);
        Assert.Contains("songIndexToRemove=1", query);
        Assert.Contains("songIndexToRemove=2", query);

        // Verify re-added in correct order with replacement
        var addParams = query.Split("songIdToAdd=").Skip(1).Select(s => s.Split('&')[0]).ToList();
        Assert.Equal(3, addParams.Count);
        Assert.Equal("song-a", addParams[0]);
        Assert.Equal("new-id", addParams[1]);
        Assert.Equal("song-c", addParams[2]);
    }

    [Fact]
    public async Task MigratePlaylistEntriesAsync_UpdateFails_ContinuesToNextPlaylist()
    {
        SetupCredentials();
        var updateCallCount = 0;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getPlaylist") && !url.Contains("getPlaylists"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"subsonic-response\":{\"playlist\":{\"entry\":[{\"id\":\"old-id\",\"title\":\"Song\"}]}}}")
                    };
                }
                if (url.Contains("updatePlaylist"))
                {
                    updateCallCount++;
                    // First call fails, second succeeds
                    if (updateCallCount == 1)
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        // Should not throw - processes both playlists even if first fails
        await _service.MigratePlaylistEntriesAsync("old-id", "new-id",
            new List<(string, string)> { ("pl-1", "Playlist 1"), ("pl-2", "Playlist 2") });

        Assert.Equal(2, updateCallCount);
    }

    [Fact]
    public async Task TriggerLibraryScanAsync_WithAdminSettings_UsesAdminCredentials()
    {
        // Arrange: service configured with an admin account
        var service = BuildService(adminUsername: "configured-admin", adminPassword: "secret");

        // Also set user credentials with a DIFFERENT username so we can tell which one
        // ends up in the scan request URL.
        service.SetSubsonicCredentials(new Dictionary<string, string>
        {
            ["u"] = "request-user",
            ["t"] = "user-token",
            ["s"] = "user-salt",
            ["v"] = "1.16.1",
            ["c"] = "aonsoku"
        });

        // Mock: every getUser call returns adminRole=true (regardless of whose it asks about).
        // Capture every outgoing URL so we can inspect later which one reached startScan.
        var capturedUris = new List<Uri?>();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUris.Add(req.RequestUri))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":true}}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        // Act
        var result = await service.TriggerLibraryScanAsync();

        // Assert: scan was triggered using the admin account's username, not the request user's
        Assert.True(result);
        var scanUri = capturedUris.FirstOrDefault(u => u?.ToString().Contains("startScan") == true);
        Assert.NotNull(scanUri);
        Assert.Contains("u=configured-admin", scanUri!.Query);
        Assert.DoesNotContain("u=request-user", scanUri.Query);
    }

    [Fact]
    public async Task TriggerLibraryScanAsync_AdminSettingsButNotAdmin_FallsBackToUserWhenUserIsAdmin()
    {
        // Scenario: admin account is configured, but Navidrome currently says it is NOT admin
        // (e.g. privileges were revoked at runtime). The request user however IS admin.
        // Expected: resolver falls back to the user's credentials.
        var service = BuildService(adminUsername: "configured-admin", adminPassword: "secret");
        service.SetSubsonicCredentials(new Dictionary<string, string>
        {
            ["u"] = "request-user",
            ["t"] = "user-token",
            ["s"] = "user-salt",
            ["v"] = "1.16.1",
            ["c"] = "aonsoku"
        });

        // Mock: inspect the 'username' query parameter and return adminRole per identity.
        // - configured-admin -> adminRole=false
        // - request-user     -> adminRole=true
        var capturedUris = new List<Uri?>();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUris.Add(req.RequestUri))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    // CheckUserIsAdminAsync always queries itself (username == authenticating user),
                    // so the 'username' param identifies which credential set is being verified.
                    var isAdmin = url.Contains("username=request-user");
                    var json = $"{{\"subsonic-response\":{{\"status\":\"ok\",\"user\":{{\"adminRole\":{isAdmin.ToString().ToLower()}}}}}}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        // Act
        var result = await service.TriggerLibraryScanAsync();

        // Assert: scan went out with the user's credentials since the admin account failed the check
        Assert.True(result);
        var scanUri = capturedUris.FirstOrDefault(u => u?.ToString().Contains("startScan") == true);
        Assert.NotNull(scanUri);
        Assert.Contains("u=request-user", scanUri!.Query);
        Assert.DoesNotContain("u=configured-admin", scanUri.Query);
    }

    [Fact]
    public async Task TriggerLibraryScanAsync_NeitherAdminCapable_NoScanTriggered()
    {
        // Scenario: admin account configured but is NOT admin, and the request user is ALSO not admin.
        // Expected: no credentials with admin rights -> resolver returns null -> no scan.
        var service = BuildService(adminUsername: "configured-admin", adminPassword: "secret");
        service.SetSubsonicCredentials(new Dictionary<string, string>
        {
            ["u"] = "request-user",
            ["t"] = "user-token",
            ["s"] = "user-salt",
            ["v"] = "1.16.1",
            ["c"] = "aonsoku"
        });

        // Mock: every getUser call returns adminRole=false - nobody has admin rights.
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":false}}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        // Act
        var result = await service.TriggerLibraryScanAsync();

        // Assert: function returns false AND no startScan request was sent.
        // (getUser calls ARE allowed - we are checking that no SCAN went out specifically.)
        Assert.False(result);
        _mockHandler.Protected()
            .Verify("SendAsync", Times.Never(),
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("startScan")),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task TriggerLibraryScanAsync_CachesUserAdminCheckAcrossCalls()
    {
        // Scenario: no admin account configured - resolver uses the user fallback path.
        // After the first trigger, _userIsAdmin should be cached so subsequent triggers
        // do not re-query getUser.
        var service = BuildService();   // no admin settings -> user fallback path
        service.SetSubsonicCredentials(new Dictionary<string, string>
        {
            ["u"] = "request-user",
            ["t"] = "user-token",
            ["s"] = "user-salt",
            ["v"] = "1.16.1",
            ["c"] = "aonsoku"
        });

        var getUserCalls = 0;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";
                if (url.Contains("getUser"))
                {
                    getUserCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"adminRole\":true}}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });

        // Trigger twice. First call should hit getUser once; second call should NOT
        // (the admin-status is cached in _userIsAdmin after the first check).
        // The second call will take the debounce path for the actual scan, but that
        // does not affect the admin-check caching we are asserting here.
        await service.TriggerLibraryScanAsync();
        await service.TriggerLibraryScanAsync();

        Assert.Equal(1, getUserCalls);
    }

    // --- Helpers ---

    private void SetupCredentials()
    {
        _service.SetSubsonicCredentials(new Dictionary<string, string>
        {
            ["u"] = "admin", ["t"] = "token", ["s"] = "salt", ["v"] = "1.16.1", ["c"] = "tests"
        });
    }

    private void SetupPlaylistResponses((string Id, string Name, string[] SongIds)[] playlists)
    {
        var playlistListJson = string.Join(",", playlists.Select(p =>
            $"{{\"id\":\"{p.Id}\",\"name\":\"{p.Name}\",\"songCount\":{p.SongIds.Length}}}"));

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri?.ToString() ?? "";

                if (url.Contains("getPlaylists"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $"{{\"subsonic-response\":{{\"playlists\":{{\"playlist\":[{playlistListJson}]}}}}}}")
                    };
                }

                if (url.Contains("getPlaylist") && !url.Contains("getPlaylists"))
                {
                    foreach (var p in playlists)
                    {
                        if (url.Contains($"id={p.Id}"))
                        {
                            var entriesJson = string.Join(",", p.SongIds.Select(id =>
                                $"{{\"id\":\"{id}\",\"title\":\"Song {id}\"}}"));
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                    $"{{\"subsonic-response\":{{\"playlist\":{{\"entry\":[{entriesJson}]}}}}}}")
                            };
                        }
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
                };
            });
    }

    private LocalLibraryService BuildService(
        string? adminUsername = null,
        string? adminPassword = null
        )
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Library:DownloadPath"] = _testDownloadPath
        })
        .Build();

        var subsonicSettings = Options.Create(new SubsonicSettings
        {
            Url = "https://localhost:4533",
            AdminUsername = adminUsername,
            AdminPassword = adminPassword
        });

        var mockLogger = new Mock<ILogger<LocalLibraryService>>();

        return new LocalLibraryService(
            configuration,
            _mockHttpClientFactory.Object,
            _mockMetadataService.Object,
            subsonicSettings,
            mockLogger.Object
        );
    }
}
