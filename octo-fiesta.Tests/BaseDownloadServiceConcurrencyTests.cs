using System.Collections.Concurrent;
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

public class BaseDownloadServiceConcurrencyTests : IDisposable
{
    private const string Provider = "fake";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-concurrency-tests-" + Guid.NewGuid());
    private readonly ConcurrentDictionary<string, LocalSongMapping> _mappings = new();

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    [Fact]
    public async Task SameSongRequestedTwice_DownloadsOnceAndBothGetThePath()
    {
        var service = CreateService(maxConcurrentDownloads: 2);

        var first = service.DownloadSongAsync(Provider, "track-1");
        await service.WaitForStartedAsync("track-1");
        var second = service.DownloadSongAsync(Provider, "track-1");
        service.Release("track-1");

        var paths = await Task.WhenAll(first, second).WaitAsync(Timeout);

        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(1, service.DownloadCount("track-1"));
    }

    [Fact]
    public async Task FailedDownload_CanBeRetried()
    {
        var service = CreateService(maxConcurrentDownloads: 1);
        service.FailNext("track-1");

        await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadSongAsync(Provider, "track-1"));
        service.Release("track-1");
        var path = await service.DownloadSongAsync(Provider, "track-1").WaitAsync(Timeout);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DifferentSongs_RunInParallelUpToTheLimit()
    {
        var service = CreateService(maxConcurrentDownloads: 2);

        var first = service.DownloadSongAsync(Provider, "track-1");
        var second = service.DownloadSongAsync(Provider, "track-2");
        await service.WaitForStartedAsync("track-1");
        await service.WaitForStartedAsync("track-2");
        service.Release("track-1");
        service.Release("track-2");

        await Task.WhenAll(first, second).WaitAsync(Timeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task InvalidLimit_FallsBackToOneDownloadAtATime(int maxConcurrentDownloads)
    {
        var service = CreateService(maxConcurrentDownloads);

        var first = service.DownloadSongAsync(Provider, "track-1");
        await service.WaitForStartedAsync("track-1");
        var second = service.DownloadSongAsync(Provider, "track-2");

        await Task.Delay(200);
        Assert.False(service.HasStarted("track-2"));

        service.Release("track-1");
        service.Release("track-2");
        await Task.WhenAll(first, second).WaitAsync(Timeout);
    }

    [Fact]
    public async Task LimitAboveTheCap_IsCappedAtFive()
    {
        var service = CreateService(maxConcurrentDownloads: 50);
        var trackIds = Enumerable.Range(1, 6).Select(i => $"track-{i}").ToList();

        var downloads = trackIds.Select(id => service.DownloadSongAsync(Provider, id)).ToList();
        foreach (var id in trackIds.Take(5))
        {
            await service.WaitForStartedAsync(id);
        }

        await Task.Delay(200);
        Assert.False(service.HasStarted("track-6"));

        trackIds.ForEach(service.Release);
        await Task.WhenAll(downloads).WaitAsync(Timeout);
    }

    private GatedDownloadService CreateService(int maxConcurrentDownloads)
    {
        var localLibraryServiceMock = new Mock<ILocalLibraryService>();
        localLibraryServiceMock
            .Setup(x => x.GetMappingForExternalSongAsync(Provider, It.IsAny<string>()))
            .ReturnsAsync((string _, string id) => _mappings.GetValueOrDefault(id));
        localLibraryServiceMock
            .Setup(x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Song, string, string?>((song, localPath, quality) => _mappings[song.ExternalId!] = new LocalSongMapping
            {
                ExternalProvider = Provider,
                ExternalId = song.ExternalId!,
                LocalPath = localPath,
                DownloadedQuality = quality
            })
            .Returns(Task.CompletedTask);

        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.GetSongAsync(Provider, It.IsAny<string>()))
            .ReturnsAsync((string _, string id) => new Song
            {
                ExternalProvider = Provider,
                ExternalId = id,
                Title = "Title " + id,
                Artist = "Artist",
                Album = "Album"
            });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        return new GatedDownloadService(
            new Mock<IHttpClientFactory>().Object,
            config,
            localLibraryServiceMock.Object,
            metadataServiceMock.Object,
            new SubsonicSettings { MaxConcurrentDownloads = maxConcurrentDownloads },
            new Mock<IServiceProvider>().Object,
            NullLogger.Instance);
    }

    /// <summary>
    /// Download service whose provider download blocks until the test releases the track.
    /// </summary>
    private sealed class GatedDownloadService : BaseDownloadService
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _released = new();
        private readonly ConcurrentDictionary<string, int> _downloadCounts = new();
        private readonly ConcurrentDictionary<string, bool> _failNext = new();

        protected override string ProviderName => Provider;

        public GatedDownloadService(
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

        public Task WaitForStartedAsync(string trackId) => Gate(_started, trackId).Task.WaitAsync(Timeout);

        public bool HasStarted(string trackId) => Gate(_started, trackId).Task.IsCompleted;

        public void Release(string trackId) => Gate(_released, trackId).TrySetResult();

        public void FailNext(string trackId) => _failNext[trackId] = true;

        public int DownloadCount(string trackId) => _downloadCounts.GetValueOrDefault(trackId);

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => "MP3_320";

        protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
        {
            if (_failNext.TryRemove(trackId, out _))
            {
                throw new HttpRequestException("provider error");
            }

            _downloadCounts.AddOrUpdate(trackId, 1, (_, count) => count + 1);
            Gate(_started, trackId).TrySetResult();
            await Gate(_released, trackId).Task;

            var stream = new MemoryStream(new byte[] { 0x49, 0x44, 0x33 });
            return new DownloadResult(stream, "mp3", "MP3_320");
        }

        private static TaskCompletionSource Gate(ConcurrentDictionary<string, TaskCompletionSource> gates, string trackId) =>
            gates.GetOrAdd(trackId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
