using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.YouTube;
using YtDlp = octo_fiesta.Services.YouTube.YtDlpProcessRunner;

namespace octo_fiesta.Tests;

/// <summary>
/// Covers the search pipeline added to fix two real bugs found in production:
/// (1) a plain YouTube search mixed in non-music content (reaction videos, streams,
///     gameplay) for artists who also stream/vlog, and
/// (2) resolving each song's metadata sequentially blew past clients' search timeouts.
/// The fix queries YouTube Music's own "Songs" filter for an ID allowlist, then
/// resolves each ID's full metadata in parallel via GetSongAsync.
/// </summary>
public class YouTubeMetadataServiceTests
{
    private readonly Mock<IYtDlpProcessRunner> _runnerMock = new();
    private readonly Mock<ILogger<YouTubeMetadataService>> _loggerMock = new();

    private YouTubeMetadataService CreateService(YouTubeSettings? settings = null)
    {
        return new YouTubeMetadataService(
            Options.Create(settings ?? new YouTubeSettings { YtDlpPath = "yt-dlp" }),
            _runnerMock.Object,
            _loggerMock.Object);
    }

    private static bool ArgsContain(IReadOnlyList<string> args, string fragment) =>
        args.Any(a => a.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Flat-playlist "Songs" search response: id/title/url only, no artist/duration/thumbnail.</summary>
    private static string SongsSearchJson(params (string Id, string Title)[] entries)
    {
        var entriesJson = string.Join(",", entries.Select(e =>
            $$"""{"title":"{{e.Title}}","ie_key":"Youtube","id":"{{e.Id}}","_type":"url","url":"https://music.youtube.com/watch?v={{e.Id}}"}"""));
        return $$"""{"id":"query - songs","title":"query - songs","_type":"playlist","entries":[{{entriesJson}}]}""";
    }

    /// <summary>Full single-video metadata as returned by a non-flat --dump-single-json.</summary>
    private static string VideoInfoJson(
        string id, string title, string channel, string channelId,
        int duration = 200, string uploadDate = "20260115", string thumbnail = "https://i.ytimg.com/vi/x/hq.jpg")
    {
        return $$"""
        {
            "id": "{{id}}",
            "title": "{{title}}",
            "channel": "{{channel}}",
            "channel_id": "{{channelId}}",
            "uploader": "{{channel}}",
            "duration": {{duration}},
            "upload_date": "{{uploadDate}}",
            "thumbnails": [{"url": "https://i.ytimg.com/vi/x/lo.jpg", "height": 90, "width": 120}, {"url": "{{thumbnail}}", "height": 720, "width": 1280}]
        }
        """;
    }

    private void SetupRunner(string urlFragment, int exitCode, string stdout, string stderr = "")
    {
        _runnerMock
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(args => ArgsContain(args, urlFragment)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YtDlp.ExecutionResult(exitCode, stdout, stderr));
    }

    // ---- SearchSongsAsync: guard clauses ----

    [Fact]
    public async Task SearchSongsAsync_WithShortQuery_ReturnsEmptyWithoutCallingRunner()
    {
        var service = CreateService();

        var result = await service.SearchSongsAsync("ab");

        Assert.Empty(result);
        _runnerMock.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchSongsAsync_WhenDisabled_ReturnsEmptyWithoutCallingRunner()
    {
        var service = CreateService(new YouTubeSettings { Enabled = false, YtDlpPath = "yt-dlp" });

        var result = await service.SearchSongsAsync("a valid query");

        Assert.Empty(result);
        _runnerMock.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- SearchSongsAsync: the actual search pipeline ----

    [Fact]
    public async Task SearchSongsAsync_QueriesTheSongsFilteredMusicSearch_NotPlainYouTubeSearch()
    {
        SetupRunner("music.youtube.com/search", 0, SongsSearchJson(("id1", "Song One")));
        SetupRunner("watch?v=id1", 0, VideoInfoJson("id1", "Song One", "Artist", "UC123"));

        var service = CreateService();
        await service.SearchSongsAsync("query");

        _runnerMock.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(args =>
                ArgsContain(args, "music.youtube.com/search") &&
                ArgsContain(args, "#songs") &&
                args.Contains("--flat-playlist") &&
                args.Contains("--playlist-end")),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Never falls back to a plain (unfiltered) ytsearch query.
        _runnerMock.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(args => args.Any(a => a.StartsWith("ytsearch"))),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchSongsAsync_ResolvesEachSongIdIndividually_AndMapsFullMetadata()
    {
        SetupRunner("music.youtube.com/search", 0, SongsSearchJson(("id1", "Song One"), ("id2", "Song Two")));
        SetupRunner("watch?v=id1", 0, VideoInfoJson("id1", "Song One", "Artist One", "UC1", duration: 180));
        SetupRunner("watch?v=id2", 0, VideoInfoJson("id2", "Song Two", "Artist Two", "UC2", duration: 210));

        var service = CreateService();
        var songs = await service.SearchSongsAsync("query");

        Assert.Equal(2, songs.Count);
        var song1 = songs.Single(s => s.ExternalId == "id1");
        Assert.Equal("Song One", song1.Title);
        Assert.Equal("Artist One", song1.Artist);
        Assert.Equal(180, song1.Duration);
        Assert.Equal("ext-youtube-artist-UC1", song1.ArtistId);
        Assert.Equal("ext-youtube-album-video-id1", song1.AlbumId);
        Assert.Equal("2026-01-15", song1.ReleaseDate);
        Assert.Equal("https://i.ytimg.com/vi/x/hq.jpg", song1.CoverArtUrl);
        Assert.Equal("youtube", song1.ExternalProvider);
    }

    [Fact]
    public async Task SearchSongsAsync_WhenMusicSearchExitsNonZero_ReturnsEmpty()
    {
        SetupRunner("music.youtube.com/search", 1, "", "network error");

        var service = CreateService();
        var songs = await service.SearchSongsAsync("query");

        Assert.Empty(songs);
    }

    [Fact]
    public async Task SearchSongsAsync_WhenMusicSearchReturnsNoEntries_ReturnsEmptyWithoutResolvingAnySong()
    {
        SetupRunner("music.youtube.com/search", 0, """{"id":"query - songs","title":"query - songs","_type":"playlist","entries":[]}""");

        var service = CreateService();
        var songs = await service.SearchSongsAsync("query");

        Assert.Empty(songs);
        _runnerMock.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(args => ArgsContain(args, "watch?v=")),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchSongsAsync_WhenIndividualSongResolutionFails_SkipsThatSongButKeepsOthers()
    {
        SetupRunner("music.youtube.com/search", 0, SongsSearchJson(("id1", "Song One"), ("id2", "Song Two")));
        SetupRunner("watch?v=id1", 1, "", "unavailable");
        SetupRunner("watch?v=id2", 0, VideoInfoJson("id2", "Song Two", "Artist Two", "UC2"));

        var service = CreateService();
        var songs = await service.SearchSongsAsync("query");

        var song = Assert.Single(songs);
        Assert.Equal("id2", song.ExternalId);
    }

    [Fact]
    public async Task SearchSongsAsync_RespectsMaxResultsClamp()
    {
        SetupRunner("music.youtube.com/search", 0, SongsSearchJson(("id1", "Song One")));
        SetupRunner("watch?v=id1", 0, VideoInfoJson("id1", "Song One", "Artist", "UC1"));

        var service = CreateService(new YouTubeSettings { YtDlpPath = "yt-dlp", MaxResults = 3 });
        await service.SearchSongsAsync("query", limit: 50);

        _runnerMock.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(args => args.Contains("3")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---- GetSongAsync / MapEntryToSong ----

    [Fact]
    public async Task GetSongAsync_WithWrongProvider_ReturnsNull()
    {
        var service = CreateService();
        var song = await service.GetSongAsync("qobuz", "id1");
        Assert.Null(song);
        _runnerMock.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSongAsync_FallsBackToChannelWhenArtistFieldMissing()
    {
        // yt-dlp video info has no top-level "artist" field for most uploads - only channel/uploader.
        SetupRunner("watch?v=id1", 0, VideoInfoJson("id1", "A Song", "Some Channel", "UCabc"));

        var service = CreateService();
        var song = await service.GetSongAsync("youtube", "id1");

        Assert.NotNull(song);
        Assert.Equal("Some Channel", song!.Artist);
    }

    [Fact]
    public async Task GetSongAsync_WithoutChannelId_BuildsSlugArtistExternalId()
    {
        var json = """
        {
            "id": "id1",
            "title": "A Song",
            "uploader": "Weird Artist Name!!",
            "duration": 120
        }
        """;
        SetupRunner("watch?v=id1", 0, json);

        var service = CreateService();
        var song = await service.GetSongAsync("youtube", "id1");

        Assert.NotNull(song);
        Assert.Equal("ext-youtube-artist-name-weird-artist-name", song!.ArtistId);
    }

    [Fact]
    public async Task GetAlbumAsync_ResolvesUnderlyingVideoAndWrapsAsSingleTrackAlbum()
    {
        SetupRunner("watch?v=id1", 0, VideoInfoJson("id1", "A Song", "Artist", "UC1"));

        var service = CreateService();
        var album = await service.GetAlbumAsync("youtube", "video-id1");

        Assert.NotNull(album);
        Assert.Equal("YouTube", album!.Title); // YouTube videos have no real album; the mapper defaults to this.
        Assert.Single(album.Songs);
        Assert.Equal("id1", album.Songs[0].ExternalId);
        Assert.Equal("A Song", album.Songs[0].Title);
    }

    [Fact]
    public async Task GetAlbumAsync_WithNonVideoAlbumExternalId_ReturnsNull()
    {
        var service = CreateService();
        var album = await service.GetAlbumAsync("youtube", "not-a-video-prefix-id1");
        Assert.Null(album);
    }
}
