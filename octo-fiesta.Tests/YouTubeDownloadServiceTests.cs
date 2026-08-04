using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.YouTube;
using YtDlp = octo_fiesta.Services.YouTube.YtDlpProcessRunner;

namespace octo_fiesta.Tests;

public class YouTubeDownloadServiceTests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();
    private readonly Mock<ILocalLibraryService> _localLibraryServiceMock = new();
    private readonly Mock<IMusicMetadataService> _metadataServiceMock = new();
    private readonly Mock<IYtDlpProcessRunner> _runnerMock = new();
    private readonly Mock<ILogger<YouTubeDownloadService>> _loggerMock = new();
    private readonly IConfiguration _configuration;
    private readonly string _testDownloadPath;

    public YouTubeDownloadServiceTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-youtube-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _testDownloadPath })
            .Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            try { Directory.Delete(_testDownloadPath, true); } catch { }
        }
    }

    private TestableYouTubeDownloadService CreateService(YouTubeSettings? settings = null)
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(octo_fiesta.Services.Subsonic.PlaylistSyncService)))
            .Returns((object?)null);

        return new TestableYouTubeDownloadService(
            _httpClientFactoryMock.Object,
            _configuration,
            _localLibraryServiceMock.Object,
            _metadataServiceMock.Object,
            Options.Create(new SubsonicSettings()),
            Options.Create(settings ?? new YouTubeSettings { YtDlpPath = "yt-dlp" }),
            _runnerMock.Object,
            serviceProviderMock.Object,
            _loggerMock.Object);
    }

    /// <summary>
    /// yt-dlp writes its output to a template path (e.g. "{trackId}.%(ext)s") on disk as a side
    /// effect - DownloadTrackAsync then reads that file back. The mocked runner has to reproduce
    /// that side effect (writing a real file) for the download flow to have anything to find.
    /// </summary>
    private void SetupSuccessfulDownload(string tempRoot, string trackId, string writtenExtension, string content = "fake-audio-bytes")
    {
        _runnerMock
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(args => args.Contains("--output")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var path = Path.Combine(tempRoot, $"{trackId}{writtenExtension}");
                File.WriteAllText(path, content);
                return new YtDlp.ExecutionResult(0, "", "");
            });
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "octo-fiesta-youtube");

    // ---- DownloadTrackAsync: the filename-mangling regression ----
    //
    // Bug: extension was stripped of its leading dot before being handed to
    // PathHelper.BuildTrackPath (which documents the parameter as "including the dot"),
    // producing "...Titlem4a.m4a" instead of "...Title.m4a" once the format-mismatch
    // corrector appended a second, correct extension. Fixed by keeping the dot.

    [Fact]
    public async Task DownloadTrackAsync_ReturnsExtensionWithLeadingDot()
    {
        var trackId = "regressionTest1";
        SetupSuccessfulDownload(TempRoot(), trackId, ".m4a");
        var service = CreateService();
        var song = new Song { Title = "Some Title", ExternalId = trackId };

        var result = await service.CallDownloadTrackAsync(trackId, song, CancellationToken.None);

        Assert.StartsWith(".", result.Extension);
        Assert.Equal(".m4a", result.Extension);
        Assert.Equal("M4A", result.DownloadedQuality);
        await result.DownloadStream.DisposeAsync();
    }

    [Fact]
    public async Task DownloadTrackAsync_WithMp3Output_ReturnsMp3ExtensionWithDot()
    {
        var trackId = "regressionTest2";
        SetupSuccessfulDownload(TempRoot(), trackId, ".mp3");
        var service = CreateService();
        var song = new Song { Title = "Some Title", ExternalId = trackId };

        var result = await service.CallDownloadTrackAsync(trackId, song, CancellationToken.None);

        Assert.Equal(".mp3", result.Extension);
        await result.DownloadStream.DisposeAsync();
    }

    [Fact]
    public async Task DownloadTrackAsync_WhenYtDlpExitsNonZero_ThrowsWithStderr()
    {
        var trackId = "regressionTest3";
        _runnerMock
            .Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YtDlp.ExecutionResult(1, "", "video unavailable"));
        var service = CreateService();
        var song = new Song { Title = "Some Title", ExternalId = trackId };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CallDownloadTrackAsync(trackId, song, CancellationToken.None));
        Assert.Contains("video unavailable", ex.Message);
    }

    [Fact]
    public async Task DownloadTrackAsync_PassesCookiesArgument_WhenConfigured()
    {
        var trackId = "regressionTest4";
        SetupSuccessfulDownload(TempRoot(), trackId, ".m4a");
        var cookiesPath = Path.Combine(_testDownloadPath, "cookies.txt");
        var service = CreateService(new YouTubeSettings { YtDlpPath = "yt-dlp", CookiesPath = cookiesPath });
        var song = new Song { Title = "Some Title", ExternalId = trackId };

        var result = await service.CallDownloadTrackAsync(trackId, song, CancellationToken.None);
        await result.DownloadStream.DisposeAsync();

        _runnerMock.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(args => args.Contains("--cookies") && args.Contains(cookiesPath)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---- IsAvailableAsync ----

    [Fact]
    public async Task IsAvailableAsync_WhenDisabled_ReturnsFalseWithoutCallingRunner()
    {
        var service = CreateService(new YouTubeSettings { Enabled = false, YtDlpPath = "yt-dlp" });
        var available = await service.IsAvailableAsync();
        Assert.False(available);
        _runnerMock.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsAvailableAsync_WhenYtDlpVersionCheckSucceeds_ReturnsTrue()
    {
        _runnerMock
            .Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YtDlp.ExecutionResult(0, "2026.07.04", ""));
        var service = CreateService();

        Assert.True(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_WhenYtDlpVersionCheckFails_ReturnsFalse()
    {
        _runnerMock
            .Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YtDlp.ExecutionResult(127, "", "not found"));
        var service = CreateService();

        Assert.False(await service.IsAvailableAsync());
    }

    // ---- ExtractExternalIdFromAlbumId ----

    [Fact]
    public void ExtractExternalIdFromAlbumId_WithValidYouTubeAlbumId_ReturnsExternalId()
    {
        var service = CreateService();
        var result = service.CallExtractExternalIdFromAlbumId("ext-youtube-album-video-abc123");
        Assert.Equal("video-abc123", result);
    }

    [Fact]
    public void ExtractExternalIdFromAlbumId_WithUnrelatedPrefix_ReturnsNull()
    {
        var service = CreateService();
        var result = service.CallExtractExternalIdFromAlbumId("ext-qobuz-album-123");
        Assert.Null(result);
    }

    /// <summary>Exposes protected members for direct testing without going through the full public download pipeline.</summary>
    private sealed class TestableYouTubeDownloadService : YouTubeDownloadService
    {
        public TestableYouTubeDownloadService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILocalLibraryService localLibraryService,
            IMusicMetadataService metadataService,
            IOptions<SubsonicSettings> subsonicSettings,
            IOptions<YouTubeSettings> youtubeSettings,
            IYtDlpProcessRunner processRunner,
            IServiceProvider serviceProvider,
            ILogger<YouTubeDownloadService> logger)
            : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings, youtubeSettings, processRunner, serviceProvider, logger)
        {
        }

        public Task<DownloadResult> CallDownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken) =>
            DownloadTrackAsync(trackId, song, cancellationToken);

        public string? CallExtractExternalIdFromAlbumId(string albumId) => ExtractExternalIdFromAlbumId(albumId);
    }
}
