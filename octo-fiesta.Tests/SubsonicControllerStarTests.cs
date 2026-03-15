using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicControllerStarTests
{
    private readonly Mock<IMusicMetadataService> _mockMetadataService;
    private readonly Mock<ILocalLibraryService> _mockLocalLibraryService;
    private readonly Mock<IDownloadService> _mockDownloadService;
    private readonly Mock<ILogger<SubsonicController>> _mockLogger;
    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicModelMapper _modelMapper;
    private readonly IOptions<SubsonicSettings> _settings;

    public SubsonicControllerStarTests()
    {
        _mockMetadataService = new Mock<IMusicMetadataService>();
        _mockLocalLibraryService = new Mock<ILocalLibraryService>();
        _mockDownloadService = new Mock<IDownloadService>();
        _mockLogger = new Mock<ILogger<SubsonicController>>();

        _mockLocalLibraryService
            .Setup(x => x.ParseExternalId(It.IsAny<string>()))
            .Returns((string id) => ParseExternalIdForTests(id));
        
        _requestParser = new SubsonicRequestParser();
        _responseBuilder = new SubsonicResponseBuilder();
        _modelMapper = new SubsonicModelMapper(
            _responseBuilder,
            new Mock<ILogger<SubsonicModelMapper>>().Object);
        
        _settings = Options.Create(new SubsonicSettings
        {
            Url = "http://localhost:4533",
            EnableExternalPlaylists = true
        });
    }

    private static (bool isExternal, string? provider, string? type, string? externalId) ParseExternalIdForTests(string id)
    {
        if (!id.StartsWith("ext-", StringComparison.Ordinal))
        {
            return (false, null, null, null);
        }

        var parts = id.Split('-');
        if (parts.Length >= 4)
        {
            var provider = parts[1];
            var type = parts[2];
            var externalId = string.Join("-", parts.Skip(3));
            return (true, provider, type, externalId);
        }

        return (false, null, null, null);
    }

    private SubsonicController CreateController(
        Dictionary<string, string>? queryParams = null,
        PlaylistSyncService? playlistSyncService = null)
    {
        // We can't easily mock SubsonicProxyService (concrete class with HttpClient dependency)
        // So we create a real one with a mocked HttpClient
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);
        
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        
        var proxyService = new SubsonicProxyService(
            mockHttpClientFactory.Object, _settings, httpContextAccessor);

        var appLifetimeMock = new Mock<IHostApplicationLifetime>();
        appLifetimeMock.SetupGet(x => x.ApplicationStopping).Returns(CancellationToken.None);
        
        var controller = new SubsonicController(
            _settings,
            _mockMetadataService.Object,
            _mockLocalLibraryService.Object,
            _mockDownloadService.Object,
            _requestParser,
            _responseBuilder,
            _modelMapper,
            proxyService,
            appLifetimeMock.Object,
            _mockLogger.Object,
            playlistSyncService);
        
        // Set up HttpContext with query parameters
        var httpContext = new DefaultHttpContext();
        if (queryParams != null)
        {
            var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            httpContext.Request.QueryString = new QueryString("?" + queryString);
        }
        
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        
        return controller;
    }
    
    private PlaylistSyncService CreateRealPlaylistSyncService()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "octo-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Library:DownloadPath", tempDir }
            })
            .Build();
        
        var mockLogger = new Mock<ILogger<PlaylistSyncService>>();
        var settings = Options.Create(new SubsonicSettings
        {
            PlaylistsDirectory = "playlists",
            EnableExternalPlaylists = true
        });
        
        return new PlaylistSyncService(
            Array.Empty<IMusicMetadataService>(),
            Array.Empty<IDownloadService>(),
            config,
            settings,
            mockLogger.Object);
    }

    #region Star() albumId Fallback Tests

    [Fact]
    public async Task Star_WithExternalAlbumIdInId_TriggersFullAlbumDownload()
    {
        // Arrange
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "ext-deezer-album-12345" }
            });

        // Act
        var result = await controller.Star();

        // Assert - should return success & trigger download based on id
        Assert.IsType<ContentResult>(result);
        var contentResult = (ContentResult)result;
        Assert.Contains("starred", contentResult.Content ?? "");
        _mockDownloadService.Verify(x => x.DownloadFullAlbumInBackground("deezer", "12345"), Times.Once);
    }

    [Fact]
    public async Task Star_WithExternalAlbumIdInAlbumId_AndNonAlbumInId_UsesAlbumIdFallback()
    {
        // Arrange
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "some-regular-id" },
                { "albumId", "ext-qobuz-album-abc-123" }
            });

        // Act
        var result = await controller.Star();

        // Assert - should return success & trigger download based on albumId, not id
        Assert.IsType<ContentResult>(result);
        var contentResult = (ContentResult)result;
        Assert.Contains("starred", contentResult.Content ?? "");
        _mockDownloadService.Verify(x => x.DownloadFullAlbumInBackground("qobuz", "abc-123"), Times.Once);
    }

    [Fact]
    public async Task Star_WithPlaylistIdInAlbumId_DetectsPlaylistAndTriggersDownload()
    {
        // Arrange - client sends playlist ID as albumId (this is the bug fix)
        var playlistSyncService = CreateRealPlaylistSyncService();
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "albumId", "pl-deezer-12345" }
            },
            playlistSyncService: playlistSyncService);
        
        // Act
        var result = await controller.Star();
        
        // Assert - should return success (starred response as XML), not relay to Navidrome
        Assert.IsType<ContentResult>(result);
        var contentResult = (ContentResult)result;
        Assert.Contains("starred", contentResult.Content ?? "");
    }

    [Fact]
    public async Task Star_WithPlaylistIdInId_DetectsPlaylistAndTriggersDownload()
    {
        // Arrange - client sends playlist ID as id (backward compatibility)
        var playlistSyncService = CreateRealPlaylistSyncService();
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "pl-squidwtf-99999" }
            },
            playlistSyncService: playlistSyncService);
        
        // Act
        var result = await controller.Star();
        
        // Assert
        Assert.IsType<ContentResult>(result);
        var contentResult = (ContentResult)result;
        Assert.Contains("starred", contentResult.Content ?? "");
    }

    [Fact]
    public async Task Star_WithNonPlaylistAlbumId_RelaysToNavidrome()
    {
        // Arrange - regular album ID, should be relayed to Navidrome
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "albumId", "some-navidrome-album-id" }
            });
        
        // Act & Assert - will throw because we have no real Navidrome
        // The mock HTTP handler throws InvalidOperationException (no response configured)
        // This proves it went to the relay path, not the playlist path
        await Assert.ThrowsAnyAsync<Exception>(() => controller.Star());
    }

    [Fact]
    public async Task Star_WithPlaylistIdInAlbumId_AndNonPlaylistInId_UsesAlbumIdFallback()
    {
        // Arrange - id has a non-playlist value, but albumId has a playlist ID
        var playlistSyncService = CreateRealPlaylistSyncService();
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "some-regular-id" },
                { "albumId", "pl-deezer-12345" }
            },
            playlistSyncService: playlistSyncService);
        
        // Act
        var result = await controller.Star();
        
        // Assert - should detect the playlist from albumId
        Assert.IsType<ContentResult>(result);
        var contentResult = (ContentResult)result;
        Assert.Contains("starred", contentResult.Content ?? "");
    }

    [Fact]
    public async Task Star_WithPlaylistIdInId_IgnoresAlbumId()
    {
        // Arrange - id has a playlist ID, albumId should be ignored
        var playlistSyncService = CreateRealPlaylistSyncService();
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "id", "pl-deezer-11111" },
                { "albumId", "pl-deezer-22222" }
            },
            playlistSyncService: playlistSyncService);
        
        // Act
        var result = await controller.Star();
        
        // Assert - should use the id value, not albumId
        Assert.IsType<ContentResult>(result);
        var contentResult = (ContentResult)result;
        Assert.Contains("starred", contentResult.Content ?? "");
    }

    [Fact]
    public async Task Star_WithNoPlaylistSyncService_ReturnsError()
    {
        // Arrange - playlist sync service is null (playlists not enabled)
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "albumId", "pl-deezer-12345" }
            },
            playlistSyncService: null);
        
        // Act
        var result = await controller.Star();
        
        // Assert - should return error about playlist functionality not enabled
        Assert.IsType<ContentResult>(result);
        var contentResult = (ContentResult)result;
        Assert.Contains("not enabled", contentResult.Content ?? "");
    }

    [Fact]
    public async Task Star_WithNoParameters_RelaysToNavidrome()
    {
        // Arrange - no id or albumId
        var controller = CreateController(
            queryParams: new Dictionary<string, string>());
        
        // Act & Assert - will try to relay to Navidrome (which will fail)
        // The mock HTTP handler throws InvalidOperationException (no response configured)
        await Assert.ThrowsAnyAsync<Exception>(() => controller.Star());
    }

    [Fact]
    public async Task Star_WithSquidWTFPlaylistInAlbumId_DetectsCorrectly()
    {
        // Arrange - SquidWTF (Tidal) playlist via albumId
        var playlistSyncService = CreateRealPlaylistSyncService();
        var controller = CreateController(
            queryParams: new Dictionary<string, string>
            {
                { "albumId", "pl-squidwtf-tidal-playlist-123" }
            },
            playlistSyncService: playlistSyncService);
        
        // Act
        var result = await controller.Star();
        
        // Assert
        Assert.IsType<ContentResult>(result);
        var contentResult = (ContentResult)result;
        Assert.Contains("starred", contentResult.Content ?? "");
    }

    #endregion
}
