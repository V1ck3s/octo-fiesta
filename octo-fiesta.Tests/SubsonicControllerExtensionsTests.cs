using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicControllerExtensionsTests
{
    [Theory]
    [InlineData("json", "rest/getOpenSubsonicExtensions.view")]
    [InlineData("xml", "rest/getOpenSubsonicExtensions")]
    public async Task ProxyDoesNotAdvertiseUnsupportedPlaybackReport(string format, string endpoint)
    {
        var body = format == "json"
            ? """{"subsonic-response":{"status":"ok","openSubsonicExtensions":[{"name":"playbackReport","versions":[1]},{"name":"transcodeDecision","versions":[1]}]}}"""
            : """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><openSubsonicExtensions name="playbackReport" versions="1"/><openSubsonicExtensions name="transcodeDecision" versions="1"/></subsonic-response>""";
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected().Setup<Task<HttpResponseMessage>>(
            "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, $"application/{format}")
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler.Object));
        var settings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });
        var responseBuilder = new SubsonicResponseBuilder();
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(x => x.ApplicationStopping).Returns(CancellationToken.None);
        var controller = new SubsonicController(
            settings,
            new Mock<IMusicMetadataService>().Object,
            new Mock<ILocalLibraryService>().Object,
            new Mock<IDownloadService>().Object,
            new SubsonicRequestParser(),
            responseBuilder,
            new SubsonicModelMapper(responseBuilder, new Mock<ILogger<SubsonicModelMapper>>().Object),
            new SubsonicProxyService(factory.Object, settings, new HttpContextAccessor()),
            lifetime.Object,
            new Mock<ILogger<SubsonicController>>().Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.QueryString = new QueryString($"?f={format}");
        controller.Request.Method = HttpMethods.Get;

        var result = Assert.IsType<FileContentResult>(await controller.GenericEndpoint(endpoint));
        var returned = Encoding.UTF8.GetString(result.FileContents);
        Assert.Contains("transcodeDecision", returned);
        Assert.DoesNotContain("playbackReport", returned);
        Assert.Contains("ok", returned);
    }
}
