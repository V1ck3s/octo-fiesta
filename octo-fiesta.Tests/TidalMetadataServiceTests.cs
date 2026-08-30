using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Tidal;

namespace octo_fiesta.Tests;

/// <summary>
/// Covers the mapping from Tidal's catalogue payloads to the domain models, and the
/// external ID format the Subsonic layer relies on.
/// </summary>
public class TidalMetadataServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _storePath;

    public TidalMetadataServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "octo-fiesta-tidal-metadata-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDirectory);
        _storePath = Path.Combine(_testDirectory, "tidal-tokens.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private TidalMetadataService CreateService(TidalStubHandler handler, SubsonicSettings? subsonicSettings = null)
    {
        handler.Respond("oauth2/token", TidalTestFactory.TokenResponse);

        return new TidalMetadataService(
            TidalTestFactory.HttpClientFactory(handler),
            TidalTestFactory.AuthService(handler, _storePath),
            Options.Create(subsonicSettings ?? new SubsonicSettings()),
            Mock.Of<ILogger<TidalMetadataService>>());
    }

    private const string TrackJson = """
        {
          "id": 77712242,
          "title": "Get Lucky",
          "version": "Radio Edit",
          "duration": 248,
          "trackNumber": 8,
          "volumeNumber": 1,
          "explicit": false,
          "isrc": "USQX91300108",
          "copyright": "2013 Daft Life Limited",
          "artist": { "id": 12345, "name": "Daft Punk", "picture": "aa-bb-cc" },
          "artists": [
            { "id": 12345, "name": "Daft Punk" },
            { "id": 67890, "name": "Pharrell Williams" }
          ],
          "album": {
            "id": 77712233,
            "title": "Random Access Memories",
            "cover": "11111111-2222-3333-4444-555555555555",
            "numberOfTracks": 13,
            "releaseDate": "2013-05-17",
            "type": "ALBUM"
          }
        }
        """;

    private static string TrackSearchResponse(string trackJson)
        => "{\"tracks\":{\"items\":[" + trackJson + "]}}";

    #region Search

    [Fact]
    public async Task SearchSongsAsync_MapsTracksToSongs()
    {
        var handler = new TidalStubHandler().Respond("/search", TrackSearchResponse(TrackJson));

        var songs = await CreateService(handler).SearchSongsAsync("get lucky");

        var song = Assert.Single(songs);
        Assert.Equal("Get Lucky (Radio Edit)", song.Title);
        Assert.Equal("Daft Punk", song.Artist);
        Assert.Equal("Random Access Memories", song.Album);
        Assert.Equal("ext-tidal-album-77712233", song.AlbumId);
        Assert.Equal("ext-tidal-artist-12345", song.ArtistId);
        Assert.Equal("tidal", song.ExternalProvider);
        Assert.Equal("77712242", song.ExternalId);
        Assert.Equal(248, song.Duration);
        Assert.Equal(8, song.Track);
        Assert.Equal(1, song.DiscNumber);
        Assert.Equal(13, song.TotalTracks);
        Assert.Equal(2013, song.Year);
        Assert.Equal("2013-05-17", song.ReleaseDate);
        Assert.Equal("USQX91300108", song.Isrc);
    }

    [Fact]
    public async Task SearchSongsAsync_KeepsEveryIdentifiedArtist()
    {
        var handler = new TidalStubHandler().Respond("/search", TrackSearchResponse(TrackJson));

        var song = Assert.Single(await CreateService(handler).SearchSongsAsync("get lucky"));

        Assert.Equal(
            [("ext-tidal-artist-12345", "Daft Punk"), ("ext-tidal-artist-67890", "Pharrell Williams")],
            song.Artists.Select(a => (a.Id, a.Name)));
    }

    [Fact]
    public async Task SearchSongsAsync_BuildsCoverUrlsFromTheCoverId()
    {
        var handler = new TidalStubHandler().Respond("/search", TrackSearchResponse(TrackJson));

        var song = Assert.Single(await CreateService(handler).SearchSongsAsync("get lucky"));

        Assert.Equal(
            "https://resources.tidal.com/images/11111111/2222/3333/4444/555555555555/320x320.jpg",
            song.CoverArtUrl);
        Assert.Equal(
            "https://resources.tidal.com/images/11111111/2222/3333/4444/555555555555/1280x1280.jpg",
            song.CoverArtUrlLarge);
    }

    [Fact]
    public async Task SearchAllAsync_AsksForTheThreeTypesInOneCall()
    {
        var handler = new TidalStubHandler().Respond("/search", """
            {
              "tracks": { "items": [] },
              "albums": { "items": [{ "id": 1, "title": "Album", "artist": { "id": 2, "name": "Artist" } }] },
              "artists": { "items": [{ "id": 2, "name": "Artist" }] }
            }
            """);

        var result = await CreateService(handler).SearchAllAsync("query");

        Assert.Equal("ext-tidal-album-1", Assert.Single(result.Albums).Id);
        Assert.Equal("ext-tidal-artist-2", Assert.Single(result.Artists).Id);

        var searchUrl = handler.Requests.Last().RequestUri!.ToString();
        Assert.Contains("types=TRACKS,ALBUMS,ARTISTS", Uri.UnescapeDataString(searchUrl));
    }

    [Fact]
    public async Task SearchSongsAsync_WhenTidalRejectsTheCall_ReturnsEmpty()
    {
        var handler = new TidalStubHandler().Respond("/search", """{"status":401}""", HttpStatusCode.Unauthorized);

        Assert.Empty(await CreateService(handler).SearchSongsAsync("query"));
    }

    #endregion

    #region Album and playlist

    [Fact]
    public async Task GetAlbumAsync_FillsTracksWithTheAlbumMetadata()
    {
        var handler = new TidalStubHandler()
            .Respond("/albums/77712233/items", $$"""
                {
                  "items": [
                    { "type": "track", "item": {{TrackJson}} },
                    { "type": "video", "item": { "id": 999, "title": "Video" } }
                  ]
                }
                """)
            .Respond("/albums/77712233", """
                {
                  "id": 77712233,
                  "title": "Random Access Memories",
                  "cover": "11111111-2222-3333-4444-555555555555",
                  "numberOfTracks": 13,
                  "releaseDate": "2013-05-17",
                  "type": "ALBUM",
                  "artist": { "id": 12345, "name": "Daft Punk" }
                }
                """);

        var album = await CreateService(handler).GetAlbumAsync("tidal", "77712233");

        Assert.Equal("ext-tidal-album-77712233", album!.Id);
        Assert.Equal(2013, album.Year);
        Assert.Equal("ALBUM", album.ReleaseType);

        // Videos carry no downloadable audio and are skipped.
        var song = Assert.Single(album.Songs);
        Assert.Equal("Daft Punk", song.AlbumArtist);
        Assert.Equal("ext-tidal-album-77712233", song.AlbumId);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_NumbersTracksInPlaylistOrder()
    {
        var handler = new TidalStubHandler().Respond("/playlists/uuid-1/items", $$"""
            {
              "totalNumberOfItems": 2,
              "items": [
                { "type": "track", "item": {{TrackJson}} },
                { "type": "track", "item": {{TrackJson}} }
              ]
            }
            """);

        var songs = await CreateService(handler).GetPlaylistTracksAsync("tidal", "uuid-1");

        Assert.Equal([1, 2], songs.Select(s => s.Track));
    }

    [Fact]
    public async Task GetPlaylistAsync_UsesThePlaylistIdFormat()
    {
        var handler = new TidalStubHandler().Respond("/playlists/uuid-1", """
            {
              "uuid": "uuid-1",
              "title": "Chill",
              "description": "Quiet things",
              "numberOfTracks": 42,
              "duration": 9000,
              "squareImage": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
              "creator": { "id": 7, "name": "TIDAL" }
            }
            """);

        var playlist = await CreateService(handler).GetPlaylistAsync("tidal", "uuid-1");

        Assert.Equal("pl-tidal-uuid-1", playlist!.Id);
        Assert.Equal("Chill", playlist.Name);
        Assert.Equal("TIDAL", playlist.CuratorName);
        Assert.Equal(42, playlist.TrackCount);
    }

    [Fact]
    public async Task SearchPlaylistsAsync_ParsesTheOffsetTidalWritesWithoutAColon()
    {
        // Tidal writes "+0000" rather than "+00:00", which System.Text.Json refuses to
        // bind to a date. Binding it as text kept playlist search from silently returning
        // nothing.
        var handler = new TidalStubHandler().Respond("/search", """
            {
              "playlists": {
                "items": [
                  {
                    "uuid": "0dfc3b10-fbdb-4419-bf54-11b90051fa6c",
                    "title": "Chill Pop",
                    "numberOfTracks": 50,
                    "duration": 9553,
                    "created": "2019-12-12T00:00:00.000+0000",
                    "lastUpdated": "2026-08-28T00:00:00.000+0000",
                    "creator": {},
                    "type": "EDITORIAL",
                    "squareImage": "9fcf7e1d-cbd2-43a6-bee3-d66ae535c2c4"
                  }
                ]
              }
            }
            """);

        var playlist = Assert.Single(await CreateService(handler).SearchPlaylistsAsync("chill"));

        Assert.Equal("pl-tidal-0dfc3b10-fbdb-4419-bf54-11b90051fa6c", playlist.Id);
        Assert.Equal("Chill Pop", playlist.Name);
        Assert.Equal(50, playlist.TrackCount);
        Assert.Equal(new DateTime(2019, 12, 12, 0, 0, 0, DateTimeKind.Utc), playlist.CreatedDate);
    }

    [Fact]
    public async Task GetArtistAlbumsAsync_MergesAlbumsWithEpsAndSingles()
    {
        var handler = new TidalStubHandler().Respond("/artists/12345/albums", request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.ToString().Contains("EPSANDSINGLES")
                    ? """{"items":[{"id":2,"title":"Single","artist":{"id":12345,"name":"Daft Punk"}}]}"""
                    : """{"items":[{"id":1,"title":"Album","artist":{"id":12345,"name":"Daft Punk"}}]}""")
            });

        var albums = await CreateService(handler).GetArtistAlbumsAsync("tidal", "12345");

        Assert.Equal(["ext-tidal-album-1", "ext-tidal-album-2"], albums.Select(a => a.Id));
    }

    #endregion

    #region Provider guard

    [Theory]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    public async Task Getters_IgnoreOtherProviders(string provider)
    {
        var service = CreateService(new TidalStubHandler());

        Assert.Null(await service.GetSongAsync(provider, "1"));
        Assert.Null(await service.GetAlbumAsync(provider, "1"));
        Assert.Null(await service.GetArtistAsync(provider, "1"));
        Assert.Empty(await service.GetArtistAlbumsAsync(provider, "1"));
        Assert.Empty(await service.GetPlaylistTracksAsync(provider, "1"));
    }

    #endregion

    #region Explicit filter

    [Fact]
    public async Task SearchSongsAsync_CleanOnly_DropsExplicitTracks()
    {
        var explicitTrack = TrackJson.Replace("\"explicit\": false", "\"explicit\": true");
        var handler = new TidalStubHandler().Respond("/search", TrackSearchResponse(explicitTrack));

        var songs = await CreateService(handler, new SubsonicSettings { ExplicitFilter = ExplicitFilter.CleanOnly })
            .SearchSongsAsync("query");

        Assert.Empty(songs);
    }

    #endregion
}
