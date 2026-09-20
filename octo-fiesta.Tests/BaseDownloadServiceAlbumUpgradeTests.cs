using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Tests;

public class BaseDownloadServiceAlbumUpgradeTests : IDisposable
{
    private const string Provider = "fake";
    private const string AlbumId = "album-1";
    private const string OwnedTrackId = "track-owned";

    private readonly string _testDownloadPath;
    private readonly string _ownedTrackPath;

    public BaseDownloadServiceAlbumUpgradeTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-album-upgrade-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);

        _ownedTrackPath = Path.Combine(_testDownloadPath, "owned.mp3");
        File.WriteAllText(_ownedTrackPath, "mp3");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    [Fact]
    public async Task DownloadFullAlbum_WhenAutoUpgradeEnabled_UpgradesTrackBelowTargetQuality()
    {
        var service = CreateService(autoUpgradeQuality: true);

        await service.DownloadAlbumAsync(AlbumId);

        Assert.Equal(new[] { OwnedTrackId }, service.DownloadedTrackIds);
    }

    [Fact]
    public async Task DownloadFullAlbum_WhenAutoUpgradeDisabled_LeavesOwnedTrackAlone()
    {
        var service = CreateService(autoUpgradeQuality: false);

        await service.DownloadAlbumAsync(AlbumId);

        Assert.Empty(service.DownloadedTrackIds);
    }

    private FakeAlbumUpgradeDownloadService CreateService(bool autoUpgradeQuality)
    {
        var mapping = new LocalSongMapping
        {
            ExternalProvider = Provider,
            ExternalId = OwnedTrackId,
            LocalPath = _ownedTrackPath,
            DownloadedQuality = "MP3_320"
        };

        var song = new Song
        {
            ExternalId = OwnedTrackId,
            Title = "Owned",
            Artist = "Artist",
            Album = "Album"
        };

        var localLibraryServiceMock = new Mock<ILocalLibraryService>();
        localLibraryServiceMock
            .Setup(x => x.GetLocalPathForExternalSongAsync(Provider, OwnedTrackId))
            .ReturnsAsync(() => mapping.LocalPath);
        localLibraryServiceMock
            .Setup(x => x.GetMappingForExternalSongAsync(Provider, OwnedTrackId))
            .ReturnsAsync(() => mapping);
        localLibraryServiceMock
            .Setup(x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Song, string, string?>((_, localPath, quality) =>
            {
                mapping.LocalPath = localPath;
                mapping.DownloadedQuality = quality;
            })
            .Returns(Task.CompletedTask);
        localLibraryServiceMock
            .Setup(x => x.TriggerLibraryScanAsync())
            .ReturnsAsync(true);

        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.GetAlbumAsync(Provider, AlbumId))
            .ReturnsAsync(new Album { Id = AlbumId, Title = "Album", Artist = "Artist", Songs = new List<Song> { song } });
        metadataServiceMock
            .Setup(x => x.GetSongAsync(Provider, OwnedTrackId))
            .ReturnsAsync(song);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        return new FakeAlbumUpgradeDownloadService(
            new Mock<IHttpClientFactory>().Object,
            config,
            localLibraryServiceMock.Object,
            metadataServiceMock.Object,
            new SubsonicSettings { AutoUpgradeQuality = autoUpgradeQuality },
            new Mock<IServiceProvider>().Object,
            NullLogger.Instance);
    }

    private sealed class FakeAlbumUpgradeDownloadService : BaseDownloadService
    {
        protected override string ProviderName => Provider;

        public List<string> DownloadedTrackIds { get; } = new();

        public FakeAlbumUpgradeDownloadService(
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

        public Task DownloadAlbumAsync(string albumExternalId) => DownloadFullAlbumAsync(albumExternalId);

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => "FLAC";

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
        {
            DownloadedTrackIds.Add(trackId);
            var stream = new MemoryStream(new byte[] { 0x66, 0x4C, 0x61, 0x43 });
            return Task.FromResult(new DownloadResult(stream, "flac", "FLAC"));
        }
    }
}
