using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

public class ExternalCoverArtServiceTests
{
    private const string Small = "https://static.qobuz.com/images/covers/ab/cd/abc_600.jpg";
    private const string Original = "https://static.qobuz.com/images/covers/ab/cd/abc_org.jpg";

    [Theory]
    [InlineData("song")]
    [InlineData("album")]
    public async Task GetAsync_ByDefault_SendsTheDisplayImage(string type)
    {
        var (service, images, _) = CreateService();

        Assert.NotNull(await service.GetAsync("qobuz", type, "1"));

        // The original is only fetched by downloads, unless COVER_ART_SIZE=Large
        Assert.Equal(new[] { Small }, images.Requests);
    }

    [Theory]
    [InlineData("album")]
    [InlineData("song")]
    public async Task GetAsync_WhenLargeIsConfigured_SendsTheLargeImage(string type)
    {
        var (service, images, _) = CreateService(size: CoverArtSize.Large);

        await service.GetAsync("qobuz", type, "1");

        Assert.Equal(new[] { Original }, images.Requests);
    }

    [Fact]
    public async Task GetAsync_AskedAgain_ServesTheCachedImage()
    {
        var (service, images, metadata) = CreateService();

        var first = await service.GetAsync("qobuz", "song", "1");
        var second = await service.GetAsync("qobuz", "song", "1");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(new[] { Small }, images.Requests);
        metadata.Verify(m => m.GetSongAsync("qobuz", "1"), Times.Once);
    }

    [Fact]
    public async Task GetAsync_ConcurrentRequests_ShareOneDownload()
    {
        var (service, images, _) = CreateService(delay: TimeSpan.FromMilliseconds(200));

        var covers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GetAsync("qobuz", "song", "1")));

        Assert.All(covers, Assert.NotNull);
        Assert.Single(images.Requests);
    }

    [Fact]
    public async Task Prefetch_HasTheCoverReadyForThePlayer()
    {
        var (service, images, _) = CreateService();

        service.Prefetch("qobuz", "1");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (images.Requests.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        await service.GetAsync("qobuz", "song", "1");

        Assert.Single(images.Requests);
    }

    [Fact]
    public async Task GetAsync_AfterAFailedDownload_TriesAgain()
    {
        var (service, images, _) = CreateService(status: HttpStatusCode.ServiceUnavailable);

        Assert.Null(await service.GetAsync("qobuz", "song", "1"));
        Assert.Null(await service.GetAsync("qobuz", "song", "1"));

        Assert.Equal(2, images.Requests.Count);
    }

    private static (ExternalCoverArtService Service, FakeImages Images, Mock<IMusicMetadataService> Metadata) CreateService(
        TimeSpan? delay = null, HttpStatusCode status = HttpStatusCode.OK, CoverArtSize size = CoverArtSize.Display)
    {
        var images = new FakeImages(delay ?? TimeSpan.Zero, status);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(images, disposeHandler: false));

        var metadata = new Mock<IMusicMetadataService>();
        metadata
            .Setup(m => m.GetSongAsync("qobuz", It.IsAny<string>()))
            .ReturnsAsync((string _, string id) => new Song { ExternalId = id, CoverArtUrl = Small, CoverArtUrlLarge = Original });
        metadata
            .Setup(m => m.GetAlbumAsync("qobuz", It.IsAny<string>()))
            .ReturnsAsync((string _, string id) => new Album { Id = id, CoverArtUrl = Small, CoverArtUrlLarge = Original });

        var service = new ExternalCoverArtService(metadata.Object, NullLogger<ExternalCoverArtService>.Instance, httpClientFactory.Object,
            Options.Create(new SubsonicSettings { CoverArtSize = size }));
        return (service, images, metadata);
    }

    private sealed class FakeImages(TimeSpan delay, HttpStatusCode status) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();

        public IReadOnlyList<string> Requests => _requests.ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!.ToString());
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent([0xFF, 0xD8, 0xFF])
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                }
            };
        }
    }
}
