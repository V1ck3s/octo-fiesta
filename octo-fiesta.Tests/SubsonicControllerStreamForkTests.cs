using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicControllerStreamForkTests
{
    [Fact]
    public async Task Stream_WithExternalSong_MarksAlbumDownloadStarted()
    {
        var localLibraryServiceMock = new Mock<ILocalLibraryService>();
        localLibraryServiceMock
            .Setup(x => x.ParseSongId(It.IsAny<string>()))
            .Returns((true, "deezer", "123"));
        localLibraryServiceMock
            .Setup(x => x.ParseExternalId("ext-deezer-album-456"))
            .Returns((true, "deezer", "album", "456"));

        var metadataServiceMock = new Mock<IMusicMetadataService>();
        metadataServiceMock
            .Setup(x => x.GetSongAsync("deezer", "123"))
            .ReturnsAsync(new Song { AlbumId = "ext-deezer-album-456" });

        var downloadServiceMock = new Mock<IDownloadService>();
        downloadServiceMock
            .Setup(x => x.DownloadAndStreamAsync("deezer", "123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((Stream)new MemoryStream([1, 2, 3]), "song.mp3"));

        var coverArtServiceMock = new Mock<IExternalCoverArtService>();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(coverArtServiceMock.Object)
                .BuildServiceProvider()
        };
        httpContext.Request.QueryString = new QueryString("?id=ext-deezer-song-123");

        var settings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var proxyService = new SubsonicProxyService(
            httpClientFactory.Object,
            settings,
            new HttpContextAccessor { HttpContext = httpContext });
        var responseBuilder = new SubsonicResponseBuilder();
        var controller = new SubsonicController(
            settings,
            metadataServiceMock.Object,
            localLibraryServiceMock.Object,
            downloadServiceMock.Object,
            new SubsonicRequestParser(),
            responseBuilder,
            new SubsonicModelMapper(responseBuilder, new Mock<ILogger<SubsonicModelMapper>>().Object),
            proxyService,
            new Mock<IHostApplicationLifetime>().Object,
            new Mock<ILogger<SubsonicController>>().Object);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await controller.Stream();

        coverArtServiceMock.Verify(
            x => x.MarkAlbumDownloadStartedAsync("deezer", "123"),
            Times.Once);
    }
}
