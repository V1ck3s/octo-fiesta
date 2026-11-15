using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicControllerCoverArtTests
{
    private const string CoverUrl = "https://cdn.example/cover_600.jpg";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];

    private readonly FakeHttp _covers = new(_ => Image());
    private readonly FakeHttp _navidrome = new(_ => Image());
    private readonly Mock<IMusicMetadataService> _metadata = new();
    private readonly Mock<ILocalLibraryService> _library = new();

    [Fact]
    public async Task ExternalSong_ReturnsTheCoverWithAClientCacheHeader()
    {
        _library.Setup(x => x.ParseExternalId("ext-deezer-song-1")).Returns((true, "deezer", "song", "1"));
        _metadata.Setup(x => x.GetSongAsync("deezer", "1")).ReturnsAsync(new Song { CoverArtUrl = CoverUrl });
        var controller = CreateController("?id=ext-deezer-song-1");

        var result = Assert.IsType<FileContentResult>(await controller.GetCoverArt());

        Assert.Equal(Jpeg, result.FileContents);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal("public, max-age=86400", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(new[] { CoverUrl }, _covers.Requests);
    }

    [Fact]
    public async Task ExternalSong_ProviderError_Returns404()
    {
        _library.Setup(x => x.ParseExternalId("ext-deezer-song-1")).Returns((true, "deezer", "song", "1"));
        _metadata.Setup(x => x.GetSongAsync("deezer", "1")).ThrowsAsync(new HttpRequestException("provider down"));
        var controller = CreateController("?id=ext-deezer-song-1");

        Assert.IsType<NotFoundResult>(await controller.GetCoverArt());
    }

    [Fact]
    public async Task ExternalPlaylist_ReturnsThePlaylistCover()
    {
        _metadata.Setup(x => x.GetPlaylistAsync("deezer", "42")).ReturnsAsync(new ExternalPlaylist { CoverUrl = CoverUrl });
        var controller = CreateController("?id=pl-deezer-42");

        var result = Assert.IsType<FileContentResult>(await controller.GetCoverArt());

        Assert.Equal(Jpeg, result.FileContents);
        Assert.Equal(new[] { CoverUrl }, _covers.Requests);
    }

    [Fact]
    public async Task LocalId_IsRelayedToTheSubsonicServer()
    {
        _library.Setup(x => x.ParseExternalId("al-123")).Returns((false, null, null, null));
        var controller = CreateController("?id=al-123&size=300");

        var result = Assert.IsType<FileContentResult>(await controller.GetCoverArt());

        Assert.Equal(Jpeg, result.FileContents);
        var request = Assert.Single(_navidrome.Requests);
        Assert.Contains("rest/getCoverArt", request);
        Assert.Contains("id=al-123", request);
        Assert.Contains("size=300", request);
        Assert.Empty(_covers.Requests);
    }

    [Fact]
    public async Task Stream_ExternalSong_PrefetchesItsCover()
    {
        _library.Setup(x => x.ParseSongId("ext-deezer-song-1")).Returns((true, "deezer", "1"));
        _metadata.Setup(x => x.GetSongAsync("deezer", "1")).ReturnsAsync(new Song { CoverArtUrl = CoverUrl });
        var controller = CreateController("?id=ext-deezer-song-1");

        await controller.Stream();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_covers.Requests.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(new[] { CoverUrl }, _covers.Requests);
    }

    private SubsonicController CreateController(string query)
    {
        var settings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });
        var responseBuilder = new SubsonicResponseBuilder();

        var navidromeFactory = new Mock<IHttpClientFactory>();
        navidromeFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_navidrome, disposeHandler: false));
        var coverFactory = new Mock<IHttpClientFactory>();
        coverFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_covers, disposeHandler: false));

        var coverArt = new ExternalCoverArtService(_metadata.Object, NullLogger<ExternalCoverArtService>.Instance,
            coverFactory.Object, settings);

        var controller = new SubsonicController(
            settings,
            _metadata.Object,
            _library.Object,
            new Mock<IDownloadService>().Object,
            new SubsonicRequestParser(),
            responseBuilder,
            new SubsonicModelMapper(responseBuilder, new Mock<ILogger<SubsonicModelMapper>>().Object),
            new SubsonicProxyService(navidromeFactory.Object, settings, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }),
            new Mock<IHostApplicationLifetime>().Object,
            new Mock<ILogger<SubsonicController>>().Object,
            coverArt: coverArt);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(query);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static HttpResponseMessage Image() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Jpeg)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
        }
    };

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();

        public IReadOnlyList<string> Requests => _requests.ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }
}
