using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using octo_fiesta.Services;

namespace octo_fiesta.Tests;

public class BaseDownloadServiceCleanupTests : IDisposable
{
    private readonly string _testDownloadPath;

    public BaseDownloadServiceCleanupTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-base-cleanup-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    [Fact]
    public async Task DownloadSongAsync_WhenCanceledDuringProviderDownload_DeletesIncompleteFile()
    {
        var localLibraryServiceMock = new Mock<ILocalLibraryService>();
        localLibraryServiceMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "123"))
            .ReturnsAsync((LocalSongMapping?)null);

        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.GetSongAsync("fake", "123"))
            .ReturnsAsync(new Song
            {
                ExternalId = "123",
                Title = "Track",
                Artist = "Artist",
                Album = "Album"
            });

        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        var serviceProviderMock = new Mock<IServiceProvider>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        var service = new FakeCleanupDownloadService(
            httpClientFactoryMock.Object,
            config,
            localLibraryServiceMock.Object,
            metadataServiceMock.Object,
            new SubsonicSettings(),
            serviceProviderMock.Object,
            NullLogger.Instance,
            _testDownloadPath);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.DownloadSongAsync("fake", "123"));

        Assert.False(File.Exists(service.LastOutputPath));
    }

    private sealed class FakeCleanupDownloadService : BaseDownloadService
    {
        private readonly string _testDownloadPath;

        public string LastOutputPath { get; private set; } = string.Empty;

        protected override string ProviderName => "fake";

        public FakeCleanupDownloadService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILocalLibraryService localLibraryService,
            IMusicMetadataService metadataService,
            SubsonicSettings subsonicSettings,
            IServiceProvider serviceProvider,
            ILogger logger,
            string testDownloadPath)
            : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings, serviceProvider, logger)
        {
            _testDownloadPath = testDownloadPath;
        }

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => null;

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
        {
            LastOutputPath = Path.Combine(_testDownloadPath, "partial-file.mp3");
            File.WriteAllText(LastOutputPath, "partial");

            try
            {
                throw new OperationCanceledException("Canceled by test");
            }
            catch (OperationCanceledException)
            {
                TryDeleteIncompleteFile(LastOutputPath);
                throw;
            }
        }
    }
}
