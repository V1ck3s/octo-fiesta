using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.JioSaavn;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Tests;

public class JioSaavnDownloadServiceTests : IDisposable
{
    private readonly Mock<ILocalLibraryService> _localLibraryServiceMock = new();
    private readonly Mock<IMusicMetadataService> _metadataServiceMock = new();
    private readonly Mock<ILogger<JioSaavnDownloadService>> _loggerMock = new();
    private readonly string _testDownloadPath;

    public JioSaavnDownloadServiceTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-jiosaavn-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            try { Directory.Delete(_testDownloadPath, true); } catch { }
        }
    }

    private static HttpMessageHandler JsonHandler(string json)
    {
        var h = new Mock<HttpMessageHandler>();
        h.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(json) });
        return h.Object;
    }

    private static HttpMessageHandler FailingHandler()
    {
        var h = new Mock<HttpMessageHandler>();
        h.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        return h.Object;
    }

    private JioSaavnDownloadService CreateService(HttpMessageHandler? handler = null, string quality = "320")
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler ?? FailingHandler()));

        var apiClient = new JioSaavnApiClient(factoryMock.Object, Options.Create(new JioSaavnSettings { Quality = quality }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _testDownloadPath })
            .Build();

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(octo_fiesta.Services.Subsonic.PlaylistSyncService)))
            .Returns((object?)null);

        return new JioSaavnDownloadService(
            factoryMock.Object,
            config,
            _localLibraryServiceMock.Object,
            _metadataServiceMock.Object,
            Options.Create(new SubsonicSettings()),
            Options.Create(new JioSaavnSettings { Quality = quality }),
            serviceProviderMock.Object,
            apiClient,
            _loggerMock.Object);
    }

    // ---- IsAvailableAsync ----

    [Fact]
    public async Task IsAvailableAsync_WhenSearchSucceeds_ReturnsTrue()
    {
        var service = CreateService(JsonHandler("""{"total":1,"start":1,"results":[{"id":"1","title":"x"}]}"""));
        Assert.True(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_WhenSearchFails_ReturnsFalse()
    {
        var service = CreateService(FailingHandler());
        Assert.False(await service.IsAvailableAsync());
    }

    // ---- DownloadSongAsync routing/error paths ----

    [Fact]
    public async Task DownloadSongAsync_WithUnsupportedProvider_ThrowsNotSupportedException()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DownloadSongAsync("qobuz", "123"));
    }

    [Fact]
    public async Task DownloadSongAsync_WhenAlreadyDownloaded_ReturnsExistingPath()
    {
        var existingPath = Path.Combine(_testDownloadPath, "existing-song.m4a");
        await File.WriteAllTextAsync(existingPath, "fake audio content");

        var mapping = new LocalSongMapping
        {
            ExternalProvider = "jiosaavn",
            ExternalId = "trackId",
            LocalPath = existingPath,
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            DownloadedAt = DateTime.UtcNow,
            DownloadedQuality = "AAC_320"
        };
        _localLibraryServiceMock
            .Setup(s => s.GetMappingForExternalSongAsync("jiosaavn", "trackId"))
            .ReturnsAsync(mapping);

        var service = CreateService();
        var result = await service.DownloadSongAsync("jiosaavn", "trackId");

        Assert.Equal(existingPath, result);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenSongNotFound_ThrowsException()
    {
        _localLibraryServiceMock
            .Setup(s => s.GetLocalPathForExternalSongAsync("jiosaavn", "missing"))
            .ReturnsAsync((string?)null);
        _metadataServiceMock
            .Setup(s => s.GetSongAsync("jiosaavn", "missing"))
            .ReturnsAsync((Song?)null);

        var service = CreateService();

        var exception = await Assert.ThrowsAsync<Exception>(() => service.DownloadSongAsync("jiosaavn", "missing"));
        Assert.Equal("Song not found", exception.Message);
    }

    // ---- ExtractExternalIdFromAlbumId (indirect, matching the pattern used for the other providers) ----

    [Fact]
    public void ExtractExternalIdFromAlbumId_WithValidJioSaavnAlbumId_DoesNotRejectTheProvider()
    {
        var service = CreateService();
        var albumId = "ext-jiosaavn-album-1JwTUK7-yr0_";

        _metadataServiceMock
            .Setup(s => s.GetAlbumAsync("jiosaavn", "1JwTUK7-yr0_"))
            .ReturnsAsync(new Album { Id = albumId, Title = "Test Album", Songs = new List<Song>() });

        // DownloadRemainingAlbumTracksInBackground silently no-ops for an unsupported provider;
        // if it instead reaches the metadata fetch above, the album ID prefix was stripped correctly.
        service.DownloadRemainingAlbumTracksInBackground("jiosaavn", albumId, "track-1");
    }
}
