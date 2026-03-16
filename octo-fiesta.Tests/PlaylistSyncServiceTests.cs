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
    /// Fake Deezer metadata service - GetType().Name contains "Deezer"
    /// </summary>
    private class FakeDeezerMetadataService : IMusicMetadataService
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
    
    /// <summary>
    /// Fake SquidWTF metadata service - GetType().Name contains "SquidWTF"
    /// </summary>
    private class FakeSquidWTFMetadataService : IMusicMetadataService
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
            new FakeDeezerMetadataService(),
            new FakeQobuzMetadataService(),
            new FakeSquidWTFMetadataService()
        });
        
        // Assert - service was created without errors
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_WithOnlySquidWTF_ResolvesCorrectly()
    {
        // Arrange & Act
        var service = CreateService(metadataServices: new IMusicMetadataService[]
        {
            new FakeSquidWTFMetadataService()
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
    public async Task DownloadFullPlaylist_WithDeezerProvider_UsesDeezerMetadataService()
    {
        // Arrange
        var deezerService = new FakeDeezerMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Test Playlist", ExternalId = "12345" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { deezerService });
        
        // Act
        await service.DownloadFullPlaylistAsync("pl-deezer-12345");
        
        // Assert - verify the Deezer metadata service was called
        Assert.Equal(1, deezerService.GetPlaylistCallCount);
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
    public async Task DownloadFullPlaylist_WithSquidWTFProvider_UsesSquidWTFMetadataService()
    {
        // Arrange - THIS is the key test for issue #144 fix
        var squidWTFService = new FakeSquidWTFMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Tidal Playlist", ExternalId = "99999" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { squidWTFService });
        
        // Act
        await service.DownloadFullPlaylistAsync("pl-squidwtf-99999");
        
        // Assert
        Assert.Equal(1, squidWTFService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithUnsupportedProvider_ThrowsNotSupportedException()
    {
        // Arrange - no metadata services registered
        var service = CreateService();
        
        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.DownloadFullPlaylistAsync("pl-deezer-12345"));
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithSquidWTFNotRegistered_ThrowsNotSupportedException()
    {
        // Arrange - only Deezer registered, but trying SquidWTF playlist
        var deezerService = new FakeDeezerMetadataService();
        var service = CreateService(metadataServices: new IMusicMetadataService[] { deezerService });
        
        // Act & Assert - before the fix, this would fail because SquidWTF wasn't resolved
        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.DownloadFullPlaylistAsync("pl-squidwtf-99999"));
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithAllProvidersRegistered_RoutesToCorrectService()
    {
        // Arrange - all three providers registered, request SquidWTF
        var deezerService = new FakeDeezerMetadataService();
        var qobuzService = new FakeQobuzMetadataService();
        var squidWTFService = new FakeSquidWTFMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Tidal Playlist", ExternalId = "99999" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[]
        {
            deezerService, qobuzService, squidWTFService
        });
        
        // Act
        await service.DownloadFullPlaylistAsync("pl-squidwtf-99999");
        
        // Assert - only SquidWTF should have been called
        Assert.Equal(0, deezerService.GetPlaylistCallCount);
        Assert.Equal(0, qobuzService.GetPlaylistCallCount);
        Assert.Equal(1, squidWTFService.GetPlaylistCallCount);
    }

    #endregion

    #region Track Playlist Cache Tests

    [Fact]
    public void AddTrackToPlaylistCache_StoresTrackCorrectly()
    {
        // Arrange
        var service = CreateService();
        var trackId = "ext-deezer-12345";
        var playlistId = "pl-deezer-67890";
        
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
        var result = service.GetPlaylistIdForTrack("ext-deezer-nonexistent");
        
        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void AddTrackToPlaylistCache_OverwritesExistingEntry()
    {
        // Arrange
        var service = CreateService();
        var trackId = "ext-deezer-12345";
        
        // Act
        service.AddTrackToPlaylistCache(trackId, "pl-deezer-first");
        service.AddTrackToPlaylistCache(trackId, "pl-deezer-second");
        
        // Assert - should have the latest value
        var result = service.GetPlaylistIdForTrack(trackId);
        Assert.Equal("pl-deezer-second", result);
    }

    [Fact]
    public void GetPlaylistIdForTrack_WithSquidWTFTrack_ReturnsCorrectPlaylistId()
    {
        // Arrange
        var service = CreateService();
        var trackId = "ext-squidwtf-track-12345";
        var playlistId = "pl-squidwtf-67890";
        
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
        var deezerService = new FakeDeezerMetadataService();
        var service = CreateService(metadataServices: new IMusicMetadataService[] { deezerService });
        
        // Act - should not throw, just return early
        await service.DownloadFullPlaylistAsync("not-a-playlist-id");
        
        // Assert - no metadata service should have been called
        Assert.Equal(0, deezerService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithNullPlaylist_ReturnsEarly()
    {
        // Arrange
        var deezerService = new FakeDeezerMetadataService
        {
            PlaylistToReturn = null // playlist not found
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { deezerService });
        
        // Act
        await service.DownloadFullPlaylistAsync("pl-deezer-12345");
        
        // Assert
        Assert.Equal(1, deezerService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithEmptyTracks_ReturnsEarly()
    {
        // Arrange
        var deezerService = new FakeDeezerMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Empty Playlist", ExternalId = "12345" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { deezerService });
        
        // Act
        await service.DownloadFullPlaylistAsync("pl-deezer-12345");
        
        // Assert
        Assert.Equal(1, deezerService.GetPlaylistCallCount);
    }

    [Fact]
    public async Task DownloadFullPlaylist_WithDeezerProvider_PassesCorrectProviderAndId()
    {
        // Arrange
        var deezerService = new FakeDeezerMetadataService
        {
            PlaylistToReturn = new ExternalPlaylist { Name = "Test", ExternalId = "12345" },
            TracksToReturn = new List<Song>()
        };
        var service = CreateService(metadataServices: new IMusicMetadataService[] { deezerService });
        
        // Act
        await service.DownloadFullPlaylistAsync("pl-deezer-12345");
        
        // Assert - verify correct provider and ID were passed
        Assert.Equal("deezer", deezerService.LastPlaylistProvider);
        Assert.Equal("12345", deezerService.LastPlaylistExternalId);
    }

    #endregion
}
