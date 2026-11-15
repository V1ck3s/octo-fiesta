using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Services.Common;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicControllerBarcodeSearchTests
{
    private static SubsonicController CreateController(
        Mock<ILocalLibraryService> localLibraryServiceMock,
        Mock<IMusicMetadataService> metadataServiceMock,
        string query,
        out List<string> capturedProxyUrls)
    {
        var requestParser = new SubsonicRequestParser();
        var responseBuilder = new SubsonicResponseBuilder();
        var modelMapper = new SubsonicModelMapper(
            responseBuilder,
            new Mock<ILogger<SubsonicModelMapper>>().Object);

        var settings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });

        var urls = new List<string>();
        capturedProxyUrls = urls;

        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => urls.Add(req.RequestUri!.ToString()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{}}}",
                    System.Text.Encoding.UTF8,
                    "application/json")
            });

        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var proxyService = new SubsonicProxyService(
            mockHttpClientFactory.Object,
            settings,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var controller = new SubsonicController(
            settings,
            metadataServiceMock.Object,
            localLibraryServiceMock.Object,
            new Mock<IDownloadService>().Object,
            requestParser,
            responseBuilder,
            modelMapper,
            proxyService,
            new Mock<IHostApplicationLifetime>().Object,
            new Mock<ILogger<SubsonicController>>().Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(query);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private static readonly Album BarcodeAlbum = new()
    {
        Id = "ext-qobuz-album-0888880127631",
        Title = "Don't Quit Your Day Job",
        Artist = "Consequence",
        IsLocal = false,
        ExternalProvider = "qobuz",
        ExternalId = "0888880127631",
        Songs = new List<Song> { new() { Duration = 200 } }
    };

    [Theory]
    [InlineData("0888880127631", true)]   // EAN-13
    [InlineData("888880127631", true)]    // UPC-A
    [InlineData("00888880127631", true)]  // GTIN-14
    [InlineData("888880127632", false)]   // bad check digit
    [InlineData("1999", false)]
    [InlineData("88888012763a", false)]
    [InlineData("", false)]
    public void IsBarcode_RecognizesValidGtins(string query, bool expected)
    {
        Assert.Equal(expected, BarcodeHelper.IsBarcode(query));
    }

    [Fact]
    public void Variants_ReturnEan13ThenUpcA()
    {
        Assert.Equal(new[] { "0888880127631", "888880127631" }, BarcodeHelper.Variants("00888880127631"));
    }

    [Fact]
    public async Task Search3_BarcodeQuery_ReturnsTheAlbumWithThatBarcode()
    {
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(x => x.GetAlbumByBarcodeAsync("888880127631")).ReturnsAsync(BarcodeAlbum);
        var controller = CreateController(new Mock<ILocalLibraryService>(), metadata,
            "?query=888880127631&f=json&albumCount=5", out _);

        var result = await controller.Search3();

        var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value));
        var albums = json.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("album");
        Assert.Equal(1, albums.GetArrayLength());
        Assert.Equal("ext-qobuz-album-0888880127631", albums[0].GetProperty("id").GetString());
        metadata.Verify(x => x.SearchAllAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Search3_UnresolvedBarcode_FallsBackToTextSearch()
    {
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(x => x.GetAlbumByBarcodeAsync(It.IsAny<string>())).ReturnsAsync((Album?)null);
        metadata.Setup(x => x.SearchAllAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new SearchResult());
        var controller = CreateController(new Mock<ILocalLibraryService>(), metadata,
            "?query=888880127631&f=json", out _);

        await controller.Search3();

        metadata.Verify(x => x.SearchAllAsync("888880127631", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Search3_FailingBarcodeLookup_FallsBackToTextSearch()
    {
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(x => x.GetAlbumByBarcodeAsync(It.IsAny<string>())).ThrowsAsync(new JsonException("not json"));
        metadata.Setup(x => x.SearchAllAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new SearchResult());
        var controller = CreateController(new Mock<ILocalLibraryService>(), metadata,
            "?query=888880127631&f=json", out _);

        var result = await controller.Search3();

        Assert.IsType<JsonResult>(result);
        metadata.Verify(x => x.SearchAllAsync("888880127631", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Search3_TextQuery_DoesNotLookUpBarcodes()
    {
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(x => x.SearchAllAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new SearchResult());
        var controller = CreateController(new Mock<ILocalLibraryService>(), metadata,
            "?query=1999&f=json", out _);

        await controller.Search3();

        metadata.Verify(x => x.GetAlbumByBarcodeAsync(It.IsAny<string>()), Times.Never);
        metadata.Verify(x => x.SearchAllAsync("1999", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }
}
