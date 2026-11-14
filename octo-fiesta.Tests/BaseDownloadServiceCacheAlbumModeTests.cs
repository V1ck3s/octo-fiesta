using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Tests;

public class BaseDownloadServiceCacheAlbumModeTests : IDisposable
{
    private const string Provider = "fake";
    private const string AlbumId = "album-1";
    private const string PlayedTrackId = "track-1";
    private const string OtherTrackId = "track-2";

    private readonly string _artist = "CacheAlbumArtist" + Guid.NewGuid().ToString("N");
    private readonly string _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-cache-album-tests-" + Guid.NewGuid());
    private readonly Mock<ILocalLibraryService> _localLibraryServiceMock = new();

    public void Dispose()
    {
        foreach (var path in new[] { _testDownloadPath, Path.Combine(PathHelper.GetCachePath(), _artist) })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public async Task DownloadSongAsync_CacheAndAlbumMode_PrefetchesRemainingTracksIntoCache()
    {
        var service = CreateService();

        var playedPath = await service.DownloadSongAsync(Provider, PlayedTrackId);

        var otherPath = await WaitForCompletedDownloadAsync(service, $"ext-{Provider}-{OtherTrackId}");
        Assert.StartsWith(PathHelper.GetCachePath(), playedPath);
        Assert.StartsWith(PathHelper.GetCachePath(), otherPath);
        Assert.True(File.Exists(otherPath));
        _localLibraryServiceMock.Verify(
            x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    private static async Task<string> WaitForCompletedDownloadAsync(FakeDownloadService service, string songId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (service.GetDownloadStatus(songId) is { Status: DownloadStatus.Completed, LocalPath: { } path })
            {
                return path;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{songId} was not downloaded");
    }

    private FakeDownloadService CreateService()
    {
        Song Track(string id, int number) => new()
        {
            ExternalProvider = Provider,
            ExternalId = id,
            Title = "Title " + id,
            Artist = _artist,
            Album = "Album",
            AlbumId = AlbumId,
            Track = number
        };

        var tracks = new List<Song> { Track(PlayedTrackId, 1), Track(OtherTrackId, 2) };

        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.GetAlbumAsync(Provider, AlbumId))
            .ReturnsAsync(new Album { Id = AlbumId, Title = "Album", Artist = _artist, Songs = tracks });
        foreach (var track in tracks)
        {
            metadataServiceMock
                .Setup(x => x.GetSongAsync(Provider, track.ExternalId!))
                .ReturnsAsync(track);
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        return new FakeDownloadService(
            new Mock<IHttpClientFactory>().Object,
            config,
            _localLibraryServiceMock.Object,
            metadataServiceMock.Object,
            new SubsonicSettings { StorageMode = StorageMode.Cache, DownloadMode = DownloadMode.Album },
            new Mock<IServiceProvider>().Object,
            NullLogger.Instance);
    }

    private sealed class FakeDownloadService : BaseDownloadService
    {
        protected override string ProviderName => Provider;

        public FakeDownloadService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILocalLibraryService localLibraryService,
            IMusicMetadataService metadataService,
            SubsonicSettings subsonicSettings,
            IServiceProvider serviceProvider,
            ILogger logger)
            : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings, serviceProvider, logger)
        {
        }

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => "MP3_320";

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
        {
            var stream = new MemoryStream(new byte[] { 0x49, 0x44, 0x33 });
            return Task.FromResult(new DownloadResult(stream, "mp3", "MP3_320"));
        }
    }
}
