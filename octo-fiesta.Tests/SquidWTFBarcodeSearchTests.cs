using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.SquidWTF;

namespace octo_fiesta.Tests;

public class SquidWTFBarcodeSearchTests
{
    private static SquidWTFMetadataService CreateService(TidalStubHandler handler, string source)
    {
        var factory = TidalTestFactory.HttpClientFactory(handler);
        var settings = Options.Create(new SquidWTFSettings
        {
            Source = source,
            Instances = new List<string> { "https://inst1.test" }
        });

        return new SquidWTFMetadataService(
            factory,
            settings,
            Options.Create(new SubsonicSettings()),
            new SquidWTFInstanceManager(factory, settings, Mock.Of<ILogger<SquidWTFInstanceManager>>()),
            Mock.Of<ILogger<SquidWTFMetadataService>>());
    }

    [Fact]
    public async Task Qobuz_PicksTheSearchResultWhoseUpcMatches()
    {
        var handler = new TidalStubHandler()
            .Respond("/api/get-music", """
                {
                  "success": true,
                  "data": { "albums": { "items": [
                    { "id": "other", "title": "Other edition", "upc": "0886443927094" },
                    { "id": "ram", "title": "Random Access Memories", "upc": "0886443927087" }
                  ] } }
                }
                """)
            .Respond("/api/get-album?album_id=ram", """
                { "success": true, "data": { "id": "ram", "title": "Random Access Memories" } }
                """);

        var album = await CreateService(handler, "Qobuz").GetAlbumByBarcodeAsync("886443927087");

        Assert.Equal("Random Access Memories", album!.Title);
    }

    [Fact]
    public async Task Qobuz_FallsBackToTheBarcodeAsAlbumId()
    {
        var handler = new TidalStubHandler()
            .Respond("/api/get-music", """{ "success": true, "data": { "albums": { "items": [] } } }""")
            .Respond("/api/get-album?album_id=0886443927087", """
                { "success": true, "data": { "id": "0886443927087", "title": "Random Access Memories" } }
                """);

        var album = await CreateService(handler, "Qobuz").GetAlbumByBarcodeAsync("886443927087");

        Assert.Equal("Random Access Memories", album!.Title);
    }

    [Fact]
    public async Task Tidal_PicksTheSearchResultWhoseUpcMatches()
    {
        var handler = new TidalStubHandler()
            .Respond("/search/", """
                {
                  "data": { "albums": { "items": [
                    { "id": 1, "title": "Other edition", "upc": "886443927094" },
                    { "id": 77712233, "title": "Random Access Memories", "upc": "886443927087" }
                  ] } }
                }
                """)
            .Respond("/album/?id=77712233", """
                { "data": { "id": 77712233, "title": "Random Access Memories", "items": [] } }
                """);

        var album = await CreateService(handler, "Tidal").GetAlbumByBarcodeAsync("0886443927087");

        Assert.Equal("Random Access Memories", album!.Title);
    }

    [Fact]
    public async Task Tidal_WhenNoUpcMatches_ReturnsNull()
    {
        var handler = new TidalStubHandler()
            .Respond("/search/", """{ "data": { "albums": { "items": [ { "id": 1, "upc": "886443927094" } ] } } }""");

        Assert.Null(await CreateService(handler, "Tidal").GetAlbumByBarcodeAsync("886443927087"));
    }
}
