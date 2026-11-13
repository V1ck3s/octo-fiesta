using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Qobuz;

namespace octo_fiesta.Tests;

public class QobuzNativeMetadataTests
{
    private static QobuzMetadataService Create(string json, ExplicitFilter filter = ExplicitFilter.All)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()).ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json) });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));
        var bundle = new Mock<QobuzBundleService>(factory.Object, NullLogger<QobuzBundleService>.Instance,
            Options.Create(new QobuzSettings()));
        bundle.Setup(b => b.GetAppIdAsync()).ReturnsAsync("test");
        return new QobuzMetadataService(factory.Object, Options.Create(new SubsonicSettings { ExplicitFilter = filter }),
            Options.Create(new QobuzSettings()), bundle.Object, NullLogger<QobuzMetadataService>.Instance);
    }

    private static string Track(int id, string warning = "") =>
        $$"""{"id":{{id}},"title":"Song","isrc":"GBTEST000001"{{warning}}} """;

    [Theory]
    [InlineData("", null)]
    [InlineData(",\"parental_warning\":null", null)]
    [InlineData(",\"parental_warning\":true", true)]
    [InlineData(",\"parental_warning\":false", false)]
    [InlineData(",\"parental_warning\":1", true)]
    [InlineData(",\"parental_warning\":0", false)]
    [InlineData(",\"parental_warning\":2", null)]
    public async Task TrackAdvisoryPreservesUnknown(string warning, bool? expected)
    {
        var song = await Create(Track(1, warning)).GetSongAsync("qobuz", "1");
        Assert.NotNull(song);
        Assert.Equal(expected, song.IsExplicit);
        Assert.Equal("GBTEST000001", song.Isrc);
    }

    [Fact]
    public async Task PreferExplicitRanksSearchAndKeepsFallbackIds()
    {
        var json = "{\"tracks\":{\"items\":[" + Track(1) + "," + Track(2, ",\"parental_warning\":false") +
            "," + Track(3, ",\"parental_warning\":true") + "]}}";
        var songs = await Create(json, ExplicitFilter.PreferExplicit).SearchSongsAsync("Song");
        Assert.Equal(new[] { "3", "1", "2" }, songs.Select(s => s.ExternalId));
        var unchanged = await Create(json).SearchSongsAsync("Song");
        Assert.Equal(new[] { "1", "2", "3" }, unchanged.Select(s => s.ExternalId));
    }

    [Fact]
    public async Task AlbumTracksCarryReleaseTagsWithoutInheritingAlbumAdvisory()
    {
        var json = "{\"id\":\"album\",\"title\":\"Album\",\"upc\":\"0012345678901\"," +
            "\"label\":{\"name\":\"Label\"},\"copyright\":\"(P) Owner\",\"parental_warning\":true," +
            "\"tracks\":{\"items\":[" + Track(1) + "," + Track(2, ",\"parental_warning\":true") + "]}}";
        var album = await Create(json, ExplicitFilter.PreferExplicit).GetAlbumAsync("qobuz", "album");
        Assert.NotNull(album);
        Assert.Equal(new[] { "1", "2" }, album.Songs.Select(s => s.ExternalId));
        Assert.Null(album.Songs[0].IsExplicit);
        Assert.All(album.Songs, song => {
            Assert.Equal("GBTEST000001", song.Isrc);
            Assert.Equal("0012345678901", song.Barcode);
            Assert.Equal("Label", song.Label);
            Assert.Equal("℗ Owner", song.Copyright);
        });
    }

    [Theory]
    [InlineData(true, "explicit")]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public void ResponsesUseIsrcArraysAndPreserveUnknownAdvisory(bool? advisory, string? expected)
    {
        var builder = new octo_fiesta.Services.Subsonic.SubsonicResponseBuilder();
        var song = new octo_fiesta.Models.Domain.Song { ExternalProvider = "qobuz", ExternalId = "1",
            Isrc = "GBTEST000001", IsExplicit = advisory };
        var json = builder.ConvertSongToJson(song);
        Assert.Equal(new[] { "GBTEST000001" }, Assert.IsType<string[]>(json["isrc"]));
        Assert.Equal(expected, json.TryGetValue("explicitStatus", out var status) ? status : null);
        System.Xml.Linq.XNamespace ns = "http://subsonic.org/restapi";
        var xml = builder.ConvertSongToXml(song, ns);
        Assert.Equal("GBTEST000001", xml.Element(ns + "isrc")?.Value);
        Assert.Equal(expected, xml.Attribute("explicitStatus")?.Value);
    }
}
