using System.Collections.Concurrent;
using System.IO.Pipelines;
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

public class BaseDownloadServiceStreamingTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] FlacHeader = "fLaC"u8.ToArray().Concat(new byte[12]).ToArray();

    private readonly string _downloadPath;

    public BaseDownloadServiceStreamingTests()
    {
        _downloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-streaming-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_downloadPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_downloadPath))
        {
            Directory.Delete(_downloadPath, true);
        }
    }

    [Fact]
    public async Task DownloadAndStreamAsync_ReturnsBeforeTheDownloadEnds_AndFollowsIt()
    {
        var service = CreateService();
        var track = service.AddTrack("1", length: FlacHeader.Length + 5);

        await track.WriteAsync(FlacHeader);
        var (stream, filePath) = await service.DownloadAndStreamAsync("fake", "1").WaitAsync(Timeout);

        await using (stream)
        {
            Assert.Equal(".flac", Path.GetExtension(filePath));
            Assert.Equal(FlacHeader.Length + 5, stream.Length);

            var head = new byte[FlacHeader.Length];
            await stream.ReadExactlyAsync(head).AsTask().WaitAsync(Timeout);
            Assert.Equal(FlacHeader, head);

            // The rest is not downloaded yet: the read waits for it
            var tail = new byte[5];
            var pendingRead = stream.ReadExactlyAsync(tail).AsTask();
            await Task.Delay(100);
            Assert.False(pendingRead.IsCompleted);

            await track.WriteAsync([1, 2, 3, 4, 5]);
            await track.CompleteAsync();
            await pendingRead.WaitAsync(Timeout);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, tail);
        }

        var status = await WaitForStatusAsync(service, "ext-fake-1", DownloadStatus.Completed);
        Assert.Equal(FlacHeader.Concat(new byte[] { 1, 2, 3, 4, 5 }), await File.ReadAllBytesAsync(status.LocalPath!));
    }

    [Fact]
    public async Task DownloadAndStreamAsync_IsNotQueuedBehindBackgroundDownloads()
    {
        var service = CreateService();
        var background = service.AddTrack("bg", length: 100);
        var played = service.AddTrack("fg", length: FlacHeader.Length);

        // An album/playlist download that is still running
        var backgroundDownload = service.DownloadSongAsync("fake", "bg");
        await background.WriteAsync(FlacHeader);
        await WaitForStatusAsync(service, "ext-fake-bg", DownloadStatus.InProgress);

        await played.WriteAsync(FlacHeader);
        await played.CompleteAsync();
        var (stream, _) = await service.DownloadAndStreamAsync("fake", "fg").WaitAsync(Timeout);
        await stream.DisposeAsync();

        Assert.False(backgroundDownload.IsCompleted);
        await background.CompleteAsync();
        await backgroundDownload.WaitAsync(Timeout);
    }

    [Fact]
    public async Task BackgroundDownloads_RunOneAtATime()
    {
        var service = CreateService();
        var first = service.AddTrack("a", length: FlacHeader.Length);
        service.AddTrack("b", length: FlacHeader.Length);

        var firstDownload = service.DownloadSongAsync("fake", "a");
        await first.WriteAsync(FlacHeader);
        await WaitForStatusAsync(service, "ext-fake-a", DownloadStatus.InProgress);

        var secondDownload = service.DownloadSongAsync("fake", "b");
        await Task.Delay(100);
        Assert.DoesNotContain("b", service.RequestedTracks);

        await first.CompleteAsync();
        await firstDownload.WaitAsync(Timeout);
        await WaitUntilAsync(() => service.RequestedTracks.Contains("b"));
        Assert.False(secondDownload.IsCompleted);
    }

    [Fact]
    public async Task DownloadAndStreamAsync_FollowsTheRunningDownloadOfTheSameTrack()
    {
        var service = CreateService();
        var track = service.AddTrack("1", length: FlacHeader.Length + 3);

        var albumDownload = service.DownloadSongAsync("fake", "1");
        await track.WriteAsync(FlacHeader);
        await WaitForStatusAsync(service, "ext-fake-1", DownloadStatus.InProgress);

        var (stream, _) = await service.DownloadAndStreamAsync("fake", "1").WaitAsync(Timeout);
        await using (stream)
        {
            await track.WriteAsync([7, 8, 9]);
            await track.CompleteAsync();

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer).WaitAsync(Timeout);
            Assert.Equal(FlacHeader.Concat(new byte[] { 7, 8, 9 }), buffer.ToArray());
        }

        await albumDownload.WaitAsync(Timeout);
        Assert.Single(service.RequestedTracks, "1");
    }

    [Fact]
    public async Task DownloadAndStreamAsync_ReaderFailsWhenTheDownloadFails()
    {
        var service = CreateService();
        var track = service.AddTrack("1", length: 1000);

        await track.WriteAsync(FlacHeader);
        var (stream, _) = await service.DownloadAndStreamAsync("fake", "1").WaitAsync(Timeout);

        await using (stream)
        {
            await track.CompleteAsync(new HttpRequestException("connection reset"));

            await Assert.ThrowsAsync<IOException>(async () =>
            {
                var buffer = new byte[1000];
                await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(Timeout);
            });
        }

        await WaitForStatusAsync(service, "ext-fake-1", DownloadStatus.Failed);
        Assert.Empty(Directory.EnumerateFiles(_downloadPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAndStreamAsync_WithoutAnnouncedSize_ServesTheFinishedFile()
    {
        var service = CreateService();
        var track = service.AddTrack("1", length: null);

        await track.WriteAsync(FlacHeader);
        var streamTask = service.DownloadAndStreamAsync("fake", "1");
        await Task.Delay(100);
        Assert.False(streamTask.IsCompleted);

        await track.CompleteAsync();
        var (stream, filePath) = await streamTask.WaitAsync(Timeout);
        await using (stream)
        {
            Assert.IsType<FileStream>(stream);
            Assert.True(File.Exists(filePath));
        }
    }

    [Fact]
    public async Task ProgressiveDownloadStream_SeekAheadWaitsForTheBytes()
    {
        var partPath = Path.Combine(_downloadPath, "seek.part");
        await using var writer = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read | FileShare.Delete, bufferSize: 0);
        var download = new ProgressiveDownload(partPath, length: 10, targetPath: "x.flac");

        await writer.WriteAsync(new byte[] { 0, 1, 2, 3 });
        download.ReportProgress(4);

        await using var reader = download.TryOpenReader()!;
        reader.Seek(8, SeekOrigin.Begin);
        var buffer = new byte[2];
        var read = reader.ReadExactlyAsync(buffer).AsTask();
        await Task.Delay(100);
        Assert.False(read.IsCompleted);

        await writer.WriteAsync(new byte[] { 4, 5, 6, 7, 8, 9 });
        download.ReportProgress(10);
        download.Complete();
        await read.WaitAsync(Timeout);
        Assert.Equal(new byte[] { 8, 9 }, buffer);
        Assert.Equal(0, await reader.ReadAsync(buffer));
    }

    [Fact]
    public async Task DownloadAndStreamAsync_WaitsForThePrebufferBeforeServing()
    {
        // 100 bytes over 10 seconds: a 2 second head start is 20 bytes
        var metadata = new Mock<IMusicMetadataService>();
        metadata
            .Setup(x => x.GetSongAsync("fake", "1"))
            .ReturnsAsync(new Song { ExternalId = "1", Title = "Track", Artist = "Artist", Album = "Album", Duration = 10 });
        var service = CreateService(metadata, prebufferSeconds: 2);
        var track = service.AddTrack("1", length: 100);

        await track.WriteAsync(FlacHeader);
        var streamTask = service.DownloadAndStreamAsync("fake", "1");
        await Task.Delay(100);
        Assert.False(streamTask.IsCompleted);

        await track.WriteAsync(new byte[4]);
        var (stream, _) = await streamTask.WaitAsync(Timeout);
        await stream.DisposeAsync();

        await track.WriteAsync(new byte[80]);
        await track.CompleteAsync();
        await WaitForStatusAsync(service, "ext-fake-1", DownloadStatus.Completed);
    }

    [Fact]
    public async Task DownloadAndStreamAsync_ShortTrack_IsServedOnceComplete()
    {
        // The head start is longer than the whole track
        var service = CreateService(prebufferSeconds: 30);
        var track = service.AddTrack("1", length: FlacHeader.Length + 2);

        await track.WriteAsync(FlacHeader);
        await track.WriteAsync([1, 2]);
        var (stream, _) = await service.DownloadAndStreamAsync("fake", "1").WaitAsync(Timeout);
        await using (stream)
        {
            Assert.Equal(FlacHeader.Length + 2, stream.Length);
        }
        await track.CompleteAsync();
    }

    [Fact]
    public async Task DownloadAndStreamAsync_AfterAFailedDownload_StartsAFreshOne()
    {
        var service = CreateService();
        var failing = service.AddTrack("1", length: null);
        await failing.CompleteAsync(new HttpRequestException("connection reset"));
        await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadAndStreamAsync("fake", "1").WaitAsync(Timeout));

        var retry = service.AddTrack("1", length: null);
        await retry.WriteAsync(FlacHeader);
        await retry.CompleteAsync();
        var (stream, _) = await service.DownloadAndStreamAsync("fake", "1").WaitAsync(Timeout);
        await stream.DisposeAsync();

        Assert.Equal(2, service.RequestedTracks.Count(id => id == "1"));
    }

    [Fact]
    public async Task AlbumDownload_TakesTrackMetadataFromTheAlbumListing()
    {
        var metadata = new Mock<IMusicMetadataService>();
        metadata
            .Setup(x => x.GetAlbumAsync("fake", "album"))
            .ReturnsAsync(new Album
            {
                Id = "album",
                Title = "Album",
                Artist = "Artist",
                Songs =
                [
                    new Song { ExternalId = "a", Title = "A", Artist = "Artist", Album = "Album" },
                    new Song { ExternalId = "b", Title = "B", Artist = "Artist", Album = "Album" }
                ]
            });
        var service = CreateService(metadata);
        foreach (var id in new[] { "a", "b" })
        {
            var track = service.AddTrack(id, length: null);
            await track.WriteAsync(FlacHeader);
            await track.CompleteAsync();
        }

        service.DownloadFullAlbumInBackground("fake", "album");
        await WaitForStatusAsync(service, "ext-fake-a", DownloadStatus.Completed);
        await WaitForStatusAsync(service, "ext-fake-b", DownloadStatus.Completed);

        metadata.Verify(x => x.GetAlbumAsync("fake", "album"), Times.Once);
        metadata.Verify(x => x.GetSongAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private FakeStreamingDownloadService CreateService(Mock<IMusicMetadataService>? metadataOverride = null, double prebufferSeconds = 0)
    {
        // Behaves like the real library: a registered download is found on the next lookup
        var registered = new ConcurrentDictionary<string, LocalSongMapping>();
        var localLibrary = new Mock<ILocalLibraryService>();
        localLibrary
            .Setup(x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((Song song, string path, string? quality) => registered[song.ExternalId!] = new LocalSongMapping
            {
                ExternalProvider = "fake",
                ExternalId = song.ExternalId!,
                LocalPath = path,
                DownloadedQuality = quality
            })
            .Returns(Task.CompletedTask);
        localLibrary
            .Setup(x => x.GetMappingForExternalSongAsync("fake", It.IsAny<string>()))
            .ReturnsAsync((string _, string id) => registered.GetValueOrDefault(id));
        var metadata = metadataOverride ?? new Mock<IMusicMetadataService>();
        if (metadataOverride == null)
        {
            metadata
                .Setup(x => x.GetSongAsync("fake", It.IsAny<string>()))
                .ReturnsAsync((string _, string id) => new Song
                {
                    ExternalId = id,
                    Title = "Track " + id,
                    Artist = "Artist",
                    Album = "Album"
                });
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _downloadPath })
            .Build();

        return new FakeStreamingDownloadService(
            new Mock<IHttpClientFactory>().Object,
            config,
            localLibrary.Object,
            metadata.Object,
            new SubsonicSettings { StreamPrebufferSeconds = prebufferSeconds },
            new Mock<IServiceProvider>().Object,
            NullLogger.Instance);
    }

    private static async Task<DownloadInfo> WaitForStatusAsync(BaseDownloadService service, string songId, DownloadStatus status)
    {
        DownloadInfo? info = null;
        await WaitUntilAsync(() => (info = service.GetDownloadStatus(songId))?.Status == status);
        return info!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time");
            }
            await Task.Delay(10);
        }
    }

    private sealed class FakeTrack(long? length)
    {
        private readonly Pipe _pipe = new();

        public long? Length { get; } = length;
        public Stream Stream => _pipe.Reader.AsStream();

        public async Task WriteAsync(byte[] bytes) => await _pipe.Writer.WriteAsync(bytes);
        public async Task CompleteAsync(Exception? error = null) => await _pipe.Writer.CompleteAsync(error);
    }

    private sealed class FakeStreamingDownloadService : BaseDownloadService
    {
        private readonly ConcurrentDictionary<string, FakeTrack> _tracks = new();

        public ConcurrentBag<string> RequestedTracks { get; } = new();

        protected override string ProviderName => "fake";

        public FakeStreamingDownloadService(
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

        public FakeTrack AddTrack(string id, long? length) => _tracks[id] = new FakeTrack(length);

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => null;

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
        {
            RequestedTracks.Add(trackId);
            var track = _tracks[trackId];
            return Task.FromResult(new DownloadResult(track.Stream, ".flac", "FLAC") { ContentLength = track.Length });
        }
    }
}
