using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.JioSaavn;
using System.Net;

namespace octo_fiesta.Tests;

/// <summary>
/// Covers the album-artist regression from production: JioSaavn lists the lead artist first in
/// every track's "primary" artists array, but featured collaborators vary per track. Before the
/// fix, AlbumArtist was never set, so the downloader fell back to the full per-track joined
/// artist string for folder naming - splitting one album into a folder per collaborator
/// combination (verified against the live API: "Villian", "Villian, Aarne", "Villian, madk1d").
/// </summary>
public class JioSaavnMetadataServiceTests
{
    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var f = new Mock<IHttpClientFactory>();
        f.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("https://example.org") });
        return f.Object;
    }

    private static HttpMessageHandler JsonHandler(string json)
    {
        var h = new Mock<HttpMessageHandler>();
        h.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            });
        return h.Object;
    }

    private static JioSaavnMetadataService CreateService(string json)
    {
        var apiClient = new JioSaavnApiClient(Factory(JsonHandler(json)), Options.Create(new JioSaavnSettings()));
        return new JioSaavnMetadataService(apiClient, Mock.Of<ILogger<JioSaavnMetadataService>>());
    }

    /// <summary>One search result "song" entry, matching JioSaavn's real API shape.</summary>
    private static string SongJson(
        string id, string title, string albumToken, string album,
        (string Id, string Name)[] primaryArtists, (string Id, string Name)[]? featuredArtists = null,
        string duration = "127", string? subtitle = null)
    {
        var primary = string.Join(",", primaryArtists.Select(a =>
            $$"""{"id":"{{a.Id}}","artist_token":"tok-{{a.Id}}","name":"{{a.Name}}","image":"","perma_url":""}"""));
        var featured = string.Join(",", (featuredArtists ?? []).Select(a =>
            $$"""{"id":"{{a.Id}}","artist_token":"tok-{{a.Id}}","name":"{{a.Name}}","image":"","perma_url":""}"""));

        return $$"""
        {
            "id": "{{id}}",
            "token": "{{id}}Token",
            "title": "{{title}}",
            "subtitle": "{{subtitle ?? $"{primaryArtists[0].Name} - {album}"}}",
            "type": "song",
            "perma_url": "https://www.jiosaavn.com/song/x/{{id}}",
            "image": "https://c.saavncdn.com/x/150x150.jpg",
            "language": "russian",
            "year": "2026",
            "isExplicit": false,
            "more_info": {
                "album_id": "album-{{albumToken}}",
                "album_token": "{{albumToken}}",
                "album": "{{album}}",
                "album_url": "https://www.jiosaavn.com/album/x/{{albumToken}}",
                "encrypted_media_url": "encrypted",
                "duration": "{{duration}}",
                "copyright_text": "(P) 2026 Test Label",
                "release_date": "2026-01-15",
                "artists": {
                    "primary": [{{primary}}],
                    "featured": [{{featured}}]
                }
            }
        }
        """;
    }

    private static string SearchResponse(params string[] songJson) =>
        $$"""{"total":{{songJson.Length}},"start":1,"results":[{{string.Join(",", songJson)}}]}""";

    // ---- AlbumArtist regression ----

    [Fact]
    public async Task SearchSongsAsync_PinsAlbumArtistToLeadArtist_EvenWhenFeaturedCollaboratorVaries()
    {
        var json = SearchResponse(
            SongJson("t1", "Track One", "alb1", "Красивое Зло", [("1", "Villian"), ("2", "Aarne")]),
            SongJson("t2", "Track Two", "alb1", "Красивое Зло", [("1", "Villian")]),
            SongJson("t3", "Track Three", "alb1", "Красивое Зло", [("1", "Villian"), ("3", "madk1d")]));
        var service = CreateService(json);

        var songs = await service.SearchSongsAsync("Красивое Зло");

        Assert.Equal(3, songs.Count);
        Assert.All(songs, s => Assert.Equal("Villian", s.AlbumArtist));
        // The full joined Artist string legitimately still differs per track (used for tagging/display).
        Assert.Equal("Villian, Aarne", songs[0].Artist);
        Assert.Equal("Villian", songs[1].Artist);
        Assert.Equal("Villian, madk1d", songs[2].Artist);
    }

    [Fact]
    public async Task SearchSongsAsync_WithSinglePrimaryArtist_AlbumArtistMatchesArtist()
    {
        var json = SearchResponse(SongJson("t1", "Solo Track", "alb1", "Solo Album", [("1", "SoloArtist")]));
        var service = CreateService(json);

        var songs = await service.SearchSongsAsync("query");

        var song = Assert.Single(songs);
        Assert.Equal("SoloArtist", song.AlbumArtist);
        Assert.Equal("SoloArtist", song.Artist);
    }

    [Fact]
    public async Task SearchSongsAsync_WithNoPrimaryArtists_FallsBackToSubtitleForBothArtistAndAlbumArtist()
    {
        var json = SearchResponse(SongJson(
            "t1", "Track", "alb1", "Album", primaryArtists: [], subtitle: "Fallback Artist - Album"));
        var service = CreateService(json);

        var songs = await service.SearchSongsAsync("query");

        var song = Assert.Single(songs);
        Assert.Equal("Fallback Artist", song.Artist);
        Assert.Equal("Fallback Artist", song.AlbumArtist);
    }

    // ---- General field mapping ----

    [Fact]
    public async Task SearchSongsAsync_MapsCoreFields()
    {
        var json = SearchResponse(SongJson(
            "t1", "Общество Мертвых Поэтов", "1JwTUK7-yr0_", "Красивое Зло",
            [("1437033", "Villian")], duration: "127"));
        var service = CreateService(json);

        var songs = await service.SearchSongsAsync("query");
        var song = Assert.Single(songs);

        Assert.Equal("Общество Мертвых Поэтов", song.Title);
        Assert.Equal("Красивое Зло", song.Album);
        Assert.Equal("ext-jiosaavn-album-1JwTUK7-yr0_", song.AlbumId);
        Assert.Equal(127, song.Duration);
        Assert.Equal(2026, song.Year);
        Assert.Equal("jiosaavn", song.ExternalProvider);
        Assert.False(song.IsLocal);
        Assert.NotNull(song.CoverArtUrl);
        Assert.Contains("500x500", song.CoverArtUrl); // 150x150 thumbnail upgraded to full-size
    }

    [Fact]
    public async Task SearchSongsAsync_MapsFeaturedArtistsAsContributors()
    {
        var json = SearchResponse(SongJson(
            "t1", "Track", "alb1", "Album", [("1", "Main")], [("2", "Feat One"), ("3", "Feat Two")]));
        var service = CreateService(json);

        var songs = await service.SearchSongsAsync("query");
        var song = Assert.Single(songs);

        Assert.Equal(["Feat One", "Feat Two"], song.Contributors);
    }

    [Fact]
    public async Task SearchSongsAsync_WithEmptyResults_ReturnsEmptyList()
    {
        var service = CreateService("""{"total":0,"start":1,"results":[]}""");
        var songs = await service.SearchSongsAsync("nonexistent query");
        Assert.Empty(songs);
    }

    [Fact]
    public async Task GetSongAsync_WithWrongProvider_ReturnsNull()
    {
        var service = CreateService("""{"total":0,"start":1,"results":[]}""");
        var song = await service.GetSongAsync("qobuz", "some-id");
        Assert.Null(song);
    }

    [Fact]
    public async Task GetAlbumAsync_IsNotSupported_ReturnsNull()
    {
        // JioSaavn's metadata service has no dedicated album endpoint wired up; downloads rely on
        // AlbumArtist being set correctly on the song itself (see the regression tests above)
        // rather than a real album fetch.
        var service = CreateService("""{"total":0,"start":1,"results":[]}""");
        var album = await service.GetAlbumAsync("jiosaavn", "some-album-id");
        Assert.Null(album);
    }
}
