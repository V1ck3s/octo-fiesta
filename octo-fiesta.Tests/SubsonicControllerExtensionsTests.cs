using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
    [InlineData("json", "rest/getOpenSubsonicExtensions.view", false)]
    [InlineData("xml", "rest/getOpenSubsonicExtensions", false)]
    [InlineData("jsonp", "rest/getOpenSubsonicExtensions.view", false)]
    [InlineData("jsonp", "rest/getOpenSubsonicExtensions", false)]
    [InlineData("jsonp", "rest/getOpenSubsonicExtensions.view", true)]
    public async Task ProxyDoesNotAdvertiseUnsupportedPlaybackReport(string format, string endpoint, bool trailingSemicolon)
    {
        var body = format != "xml"
            ? """{"subsonic-response":{"status":"ok","openSubsonicExtensions":[{"name":"playbackReport","versions":[1]},{"name":"transcoding","versions":[1]}]}}"""
            : """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><openSubsonicExtensions name="playbackReport"><versions>1</versions></openSubsonicExtensions><openSubsonicExtensions name="transcoding"><versions>1</versions></openSubsonicExtensions></subsonic-response>""";
        const string prefix = "client.onExtensions(";
        var suffix = trailingSemicolon ? ");" : ")";
        if (format == "jsonp")
        {
            body = prefix + body + suffix;
        }
        var mediaType = format == "jsonp" ? "application/javascript" : $"application/{format}";
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected().Setup<Task<HttpResponseMessage>>(
            "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
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
        controller.Request.QueryString = new QueryString($"?f={format}&callback=client.onExtensions");
        controller.Request.Method = HttpMethods.Get;

        var result = Assert.IsType<FileContentResult>(await controller.GenericEndpoint(endpoint));
        var returned = Encoding.UTF8.GetString(result.FileContents);
        Assert.Equal($"{mediaType}; charset=utf-8", result.ContentType);
        Assert.DoesNotContain("playbackReport", returned);
        if (format == "jsonp")
        {
            Assert.StartsWith(prefix, returned);
            Assert.EndsWith(suffix, returned);
            returned = returned[prefix.Length..^suffix.Length];
        }
        if (format == "xml")
        {
            var response = XDocument.Parse(returned).Root!;
            Assert.Equal("ok", response.Attribute("status")?.Value);
            var extension = Assert.Single(response.Elements());
            Assert.Equal("transcoding", extension.Attribute("name")?.Value);
            Assert.Equal("1", extension.Element(response.Name.Namespace + "versions")?.Value);
        }
        else
        {
            using var document = JsonDocument.Parse(returned);
            var response = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("ok", response.GetProperty("status").GetString());
            var extension = Assert.Single(response.GetProperty("openSubsonicExtensions").EnumerateArray());
            Assert.Equal("transcoding", extension.GetProperty("name").GetString());
            Assert.Equal(1, Assert.Single(extension.GetProperty("versions").EnumerateArray()).GetInt32());
        }
    }
}
