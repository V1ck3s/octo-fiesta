using octo_fiesta.Services.Local;
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
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"scanStatus\":{\"scanning\":false,\"count\":100}}}")
            });
        
        var httpClient = new HttpClient(_mockHandler.Object);
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
        Uri? capturedUri = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
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

        // Assert
        Assert.NotNull(capturedUri);
        var query = capturedUri!.Query;
        Assert.Contains("u=admin", query);
        Assert.Contains("t=abc123", query);
        Assert.Contains("s=xyz789", query);
        Assert.Contains("v=1.16.1", query);
        Assert.Contains("c=feishin", query);
    }

    [Fact]
    public async Task TriggerLibraryScanAsync_WithoutCredentials_SendsRequestWithoutAuth()
    {
        // Arrange
        Uri? capturedUri = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
            });

        // Act - no credentials set
        await _service.TriggerLibraryScanAsync();

        // Assert
        Assert.NotNull(capturedUri);
        var query = capturedUri!.Query;
        Assert.DoesNotContain("u=", query);
        Assert.DoesNotContain("t=", query);
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
        Uri? capturedUri = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
            });

        await _service.TriggerLibraryScanAsync();

        Assert.NotNull(capturedUri);
        Assert.Contains("u=firstuser", capturedUri!.Query);
        Assert.DoesNotContain("seconduser", capturedUri.Query);
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

        Uri? capturedUri = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\"}}")
            });

        await _service.TriggerLibraryScanAsync();

        Assert.NotNull(capturedUri);
        Assert.Contains("u=realuser", capturedUri!.Query);
    }
}
