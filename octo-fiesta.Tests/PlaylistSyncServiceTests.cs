using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class PlaylistSyncServiceTests : IDisposable
{
    private readonly Mock<ILogger<PlaylistSyncService>> _mockLogger;
    private readonly IConfiguration _configuration;
    private readonly IOptions<SubsonicSettings> _subsonicSettings;
    private readonly string _tempDir;

    public PlaylistSyncServiceTests()
    {
        // Create temp directory for downloads/playlists
        _tempDir = Path.Combine(Path.GetTempPath(), "octo-fiesta-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _mockLogger = new Mock<ILogger<PlaylistSyncService>>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Library:DownloadPath", _tempDir }
            })
            .Build();

        _subsonicSettings = Options.Create(new SubsonicSettings
        {
            PlaylistsDirectory = "playlists",
            EnableExternalPlaylists = true
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private PlaylistSyncService CreateService(
        IEnumerable<IMusicMetadataService>? metadataServices = null,
        IEnumerable<IDownloadService>? downloadServices = null)
    {
        return new PlaylistSyncService(
            metadataServices ?? Array.Empty<IMusicMetadataService>(),
            downloadServices ?? Array.Empty<IDownloadService>(),
            _configuration,
            _subsonicSettings,
            _mockLogger.Object);
    }

    #region Fake Implementations

    /// <summary>
    /// Fake Yandex metadata service - GetType().Name contains "Yandex"
    /// </summary>
    private class FakeYandexMetadataService : IMusicMetadataService
    {
        public ExternalPlaylist? PlaylistToReturn { get; set; }
        public List<Song>? TracksToReturn { get; set; }
        public int GetPlaylistCallCount { get; private set; }
        public string? LastPlaylistProvider { get; private set; }
        public string? LastPlaylistExternalId { get; private set; }

        public Task<List<Song>> SearchSongsAsync(string query, int limit = 20) => Task.FromResult(new List<Song>());
        public Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20) => Task.FromResult(new List<Album>());
        public Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20) => Task.FromResult(new List<Artist>());
        public Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
            => Task.FromResult(new SearchResult());
        public Task<Song?> GetSongAsync(string externalProvider, string externalId) => Task.FromResult<Song?>(null);
        public Task<Album?> GetAlbumAsync(string externalProvider, string externalId) => Task.FromResult<Album?>(null);
        public Task<Artist?> GetArtistAsync(string externalProvider, string externalId) => Task.FromResult<Artist?>(null);
        public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId) => Task.FromResult(new List<Album>());
        public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<ExternalPlaylist>());

        public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
        {
            GetPlaylistCallCount++;
            LastPlaylistProvider = externalProvider;
            LastPlaylistExternalId = externalId;
            return Task.FromResult(PlaylistToReturn);
        }

        public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
            => Task.FromResult(TracksToReturn ?? new List<Song>());
    }

    /// <summary>
    /// Fake Qobuz metadata service - GetType().Name contains "Qobuz"
    /// </summary>
    private class FakeQobuzMetadataService : IMusicMetadataService
    {
        public ExternalPlaylist? PlaylistToReturn { get; set; }
        public List<Song>? TracksToReturn { get; set; }
        public int GetPlaylistCallCount { get; private set; }

        public Task<List<Song>> SearchSongsAsync(string query, int limit = 20) => Task.FromResult(new List<Song>());
        public Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20) => Task.FromResult(new List<Album>());
        public Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20) => Task.FromResult(new List<Artist>());
        public Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
            => Task.FromResult(new SearchResult());
        public Task<Song?> GetSongAsync(string externalProvider, string externalId) => Task.FromResult<Song?>(null);
        public Task<Album?> GetAlbumAsync(string externalProvider, string externalId) => Task.FromResult<Album?>(null);
        public Task<Artist?> GetArtistAsync(string externalProvider, string externalId) => Task.FromResult<Artist?>(null);
        public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId) => Task.FromResult(new List<Album>());
        public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<ExternalPlaylist>());

        public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
        {
            GetPlaylistCallCount++;
            return Task.FromResult(PlaylistToReturn);
        }

        public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
            => Task.FromResult(TracksToReturn ?? new List<Song>());
    }

    #endregion

    #region Constructor / Provider Resolution Tests

    [Fact]
    public void Constructor_WithAllProviders_ResolvesAllMetadataServices()
    {
        // Arrange & Act
        var service = CreateService(metadataServices: new IMusicMetadataService[]
        {
            new FakeYandexMetadataService(),
            new FakeQobuzMetadataService()
        });

        // Assert - service was created without errors
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_WithOnlyYandex_ResolvesCorrectly()
    {
        // Arrange & Act
        var service = CreateService(metadataServices: new IMusicMetadataService[]
        {
            new FakeYandexMetadataService()
        });

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_WithNoProviders_CreatesServiceWithNullMetadata()
    {
        // Arrange & Act
        var service = CreateService();

        // Assert
        Assert.NotNull(service);
    }

    #endregion

    #region GetMetadataServiceForProvider Tests (via DownloadFullPlaylistAsync)

    [Fact]
    public async Task DownloadFullPlaylist_WithYandexProvider_UsesYandexMetadataService()
    {
        // Arrange
        var yandexService = new FakeYandexMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Test Playlist", ExternalId = "12345" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { yandexService });

        // Act
        await service.DownloadFullPlaylistAsync("pl-yandex-12345");

        // Assert - verify the Yandex metadata service was called
        Assert.Equal(1, yandexService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithQobuzProvider_UsesQobuzMetadataService()
    {
        // Arrange
        var qobuzService = new FakeQobuzMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Qobuz Playlist", ExternalId = "67890" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { qobuzService });

        // Act
        await service.DownloadFullPlaylistAsync("pl-qobuz-67890");

        // Assert
        Assert.Equal(1, qobuzService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithUnsupportedProvider_ThrowsNotSupportedException()
    {
        // Arrange - no metadata services registered
        var service = CreateService();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.DownloadFullPlaylistAsync("pl-qobuz-12345"));
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithAllProvidersRegistered_RoutesToCorrectService()
    {
        // Arrange - both providers registered, request Yandex
        var yandexService = new FakeYandexMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Yandex Playlist", ExternalId = "99999" },
            TracksToReturn = new List<Song>()
        };
        var qobuzService = new FakeQobuzMetadataService();
        var service = CreateService(metadataServices: new IMusicMetadataService[]
        {
            yandexService, qobuzService
        });

        // Act
        await service.DownloadFullPlaylistAsync("pl-yandex-99999");

        // Assert - only Yandex should have been called
        Assert.Equal(1, yandexService.GetPlaylistCallCount);
        Assert.Equal(0, qobuzService.GetPlaylistCallCount);
    }

    #endregion

    #region Track Playlist Cache Tests

    [Fact]
    public void AddTrackToPlaylistCache_StoresTrackCorrectly()
    {
        // Arrange
        var service = CreateService();
        var trackId = "ext-yandex-12345";
        var playlistId = "pl-yandex-67890";

        // Act
        service.AddTrackToPlaylistCache(trackId, playlistId);

        // Assert
        var result = service.GetPlaylistIdForTrack(trackId);
        Assert.Equal(playlistId, result);
    }

    [Fact]
    public void GetPlaylistIdForTrack_WithNonExistentTrack_ReturnsNull()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = service.GetPlaylistIdForTrack("ext-yandex-nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void AddTrackToPlaylistCache_OverwritesExistingEntry()
    {
        // Arrange
        var service = CreateService();
        var trackId = "ext-yandex-12345";

        // Act
        service.AddTrackToPlaylistCache(trackId, "pl-yandex-first");
        service.AddTrackToPlaylistCache(trackId, "pl-yandex-second");

        // Assert - should have the latest value
        var result = service.GetPlaylistIdForTrack(trackId);
        Assert.Equal("pl-yandex-second", result);
    }

    [Fact]
    public void GetPlaylistIdForTrack_WithQobuzTrack_ReturnsCorrectPlaylistId()
    {
        // Arrange
        var service = CreateService();
        var trackId = "ext-qobuz-track-12345";
        var playlistId = "pl-qobuz-67890";

        // Act
        service.AddTrackToPlaylistCache(trackId, playlistId);

        // Assert
        var result = service.GetPlaylistIdForTrack(trackId);
        Assert.Equal(playlistId, result);
    }

    #endregion

    #region DownloadFullPlaylist Integration Tests

    [Fact]
    public async Task DownloadFullPlaylist_WithInvalidPlaylistId_ReturnsEarly()
    {
        // Arrange
        var yandexService = new FakeYandexMetadataService();
        var service = CreateService(metadataServices: new IMusicMetadataService[] { yandexService });

        // Act - should not throw, just return early
        await service.DownloadFullPlaylistAsync("not-a-playlist-id");

        // Assert - no metadata service should have been called
        Assert.Equal(0, yandexService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithNullPlaylist_ReturnsEarly()
    {
        // Arrange
        var yandexService = new FakeYandexMetadataService
        {
            PlaylistToReturn = null // playlist not found
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { yandexService });

        // Act
        await service.DownloadFullPlaylistAsync("pl-yandex-12345");

        // Assert
        Assert.Equal(1, yandexService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithEmptyTracks_ReturnsEarly()
    {
        // Arrange
        var yandexService = new FakeYandexMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Empty Playlist", ExternalId = "12345" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { yandexService });

        // Act
        await service.DownloadFullPlaylistAsync("pl-yandex-12345");

        // Assert
        Assert.Equal(1, yandexService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithYandexProvider_PassesCorrectProviderAndId()
    {
        // Arrange
        var yandexService = new FakeYandexMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Test", ExternalId = "12345" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { yandexService });

        // Act
        await service.DownloadFullPlaylistAsync("pl-yandex-12345");

        // Assert - verify correct provider and ID were passed
        Assert.Equal("yandex", yandexService.LastPlaylistProvider);
        Assert.Equal("12345", yandexService.LastPlaylistExternalId);
    }

    #endregion
}
