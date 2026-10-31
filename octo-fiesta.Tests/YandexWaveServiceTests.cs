using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Yandex;
using System.Net;
using System.Text.Json;

namespace octo_fiesta.Tests;

public class YandexWaveServiceTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly List<(string Path, JsonElement Body)> _requests = new();
    private readonly Queue<string> _batches = new();
    private readonly ManualTime _time = new();
    private readonly YandexWaveService _service;
    private int _sessionsCreated;
    private string? _stationInfo;

    public YandexWaveServiceTests()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content == null
                    ? default
                    : JsonDocument.Parse(request.Content.ReadAsStringAsync().Result).RootElement.Clone();
                _requests.Add((path, body));

                string result;
                if (path.EndsWith("/info"))
                {
                    if (_stationInfo == null)
                    {
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }

                    result = _stationInfo;
                }
                else if (path.EndsWith("/feedback"))
                {
                    result = "{}";
                }
                else if (path == "/rotor/session/new")
                {
                    _sessionsCreated++;
                    result = _batches.Count > 0 ? _batches.Dequeue() : Batch($"s{_sessionsCreated}", "b0");
                }
                else
                {
                    result = _batches.Count > 0 ? _batches.Dequeue() : Batch(null, "empty");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"result\":{result}}}")
                };
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("https://example.org") });

        _service = new YandexWaveService(
            factory.Object,
            Options.Create(new YandexSettings { Language = "ru" }),
            new Mock<ILogger<YandexWaveService>>().Object,
            _time);
    }

    private static string Track(int id, int album, bool available = true) =>
        "{\"type\":\"track\",\"track\":{\"id\":\"" + id + "\",\"title\":\"T" + id + "\",\"durationMs\":180000,"
        + "\"available\":" + (available ? "true" : "false") + ",\"coverUri\":\"avatars.example.org/t" + id + "/%%\","
        + "\"artists\":[{\"id\":1,\"name\":\"Kino\"}],"
        + "\"albums\":[{\"id\":" + album + ",\"title\":\"A\",\"trackPosition\":{\"volume\":1,\"index\":1}}]}}";

    private static string Batch(string? sessionId, string batchId, params string[] tracks) =>
        $"{{{(sessionId == null ? "" : $"\"radioSessionId\":\"{sessionId}\",")}\"batchId\":\"{batchId}\",\"sequence\":[{string.Join(",", tracks)}]}}";

    private IEnumerable<(string Type, string? TrackId, int? Played)> Feedback() =>
        _requests.Where(r => r.Path.EndsWith("/feedback")).Select(r =>
        {
            var e = r.Body.GetProperty("event");
            return (
                e.GetProperty("type").GetString()!,
                e.TryGetProperty("trackId", out var t) ? t.GetString() : null,
                e.TryGetProperty("totalPlayedSeconds", out var p) ? p.GetInt32() : (int?)null);
        });

    [Fact]
    public void StationPlaylist_IsAnExternalPlaylistNamedAfterTheLanguage()
    {
        Assert.Equal("pl-yandex-wave", _service.StationPlaylist.Id);
        Assert.Equal("Моя волна", _service.StationPlaylist.Name);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(0, 1)]
    [InlineData(1000, 200)]
    public void TracksPerRead_ComesFromTheSettingsWithinBounds(int configured, int expected)
    {
        var service = new YandexWaveService(
            new Mock<IHttpClientFactory>().Object,
            Options.Create(new YandexSettings { WaveTracks = configured }),
            new Mock<ILogger<YandexWaveService>>().Object);

        Assert.Equal(expected, service.TracksPerRead);
        Assert.Equal(expected, service.StationPlaylist.TrackCount);
    }

    [Fact]
    public async Task GetStationPlaylist_UsesTheStationArtworkOnce()
    {
        _stationInfo = "[{\"station\":{\"name\":\"Моя волна\",\"fullImageUrl\":\"avatars.example.org/wave/%%\"}}]";

        var first = await _service.GetStationPlaylistAsync();
        var second = await _service.GetStationPlaylistAsync();

        Assert.Equal("https://avatars.example.org/wave/600x600", second.CoverUrl);
        Assert.Same(first, second);
        Assert.Single(_requests, r => r.Path == "/rotor/station/user:onyourwave/info");
    }

    [Fact]
    public async Task GetStationPlaylist_KeepsTheKnownArtworkAndRetriesTheLookupLater()
    {
        var known = await _service.GetStationPlaylistAsync();
        Assert.Equal("https://avatars.yandex.net/get-music-misc/70850/img.5ec4fb88d7b1560bc894e184/600x600", known.CoverUrl);

        _stationInfo = "[{\"station\":{\"fullImageUrl\":\"avatars.example.org/wave/%%\"}}]";
        await _service.GetStationPlaylistAsync();
        Assert.Single(_requests, r => r.Path == "/rotor/station/user:onyourwave/info");

        _time.Now += TimeSpan.FromMinutes(10);
        var retried = await _service.GetStationPlaylistAsync();
        Assert.Equal("https://avatars.example.org/wave/600x600", retried.CoverUrl);
    }

    [Fact]
    public async Task GetNextTracks_StartsTheSessionAndContinuesItFromTheLastTrack()
    {
        _batches.Enqueue(Batch("sid", "b1", Track(1, 10), Track(2, 20, available: false)));
        _batches.Enqueue(Batch(null, "b2", Track(3, 30), Track(4, 40)));

        var songs = await _service.GetNextTracksAsync(3);

        Assert.Equal(["1:10", "3:30", "4:40"], songs.Select(s => s.ExternalId!).ToArray());
        var newSession = _requests.First(r => r.Path == "/rotor/session/new").Body;
        Assert.Equal("user:onyourwave", newSession.GetProperty("seeds")[0].GetString());
        var next = _requests.First(r => r.Path == "/rotor/session/sid/tracks").Body;
        Assert.Equal("1:10", next.GetProperty("queue")[0].GetString());
        Assert.Equal([("radioStarted", (string?)null, (int?)null)], Feedback().ToArray());
    }

    [Fact]
    public async Task GetNextTracks_HandsEachTrackOutOnceAndQueuesEverythingHandedOut()
    {
        // batches fetched back to back overlap, and a later read may get the same tracks again
        _batches.Enqueue(Batch("sid", "b1", Track(1, 10), Track(2, 20), Track(1, 10)));
        _batches.Enqueue(Batch(null, "b2", Track(2, 20), Track(3, 30)));
        var first = await _service.GetNextTracksAsync(3);

        _batches.Enqueue(Batch(null, "b3", Track(3, 30), Track(4, 40)));
        var second = await _service.GetNextTracksAsync(1);

        Assert.Equal(["1:10", "2:20", "3:30"], first.Select(s => s.ExternalId!).ToArray());
        Assert.Equal(["4:40"], second.Select(s => s.ExternalId!).ToArray());
        var queues = _requests.Where(r => r.Path == "/rotor/session/sid/tracks")
            .Select(r => r.Body.GetProperty("queue").EnumerateArray().Select(q => q.GetString()!).ToArray())
            .ToList();
        Assert.Equal(["1:10", "2:20"], queues[0]);
        Assert.Equal(["1:10", "2:20", "3:30"], queues[1]);
    }

    [Fact]
    public async Task GetNextTracks_ReplacesAnExpiredSession()
    {
        _batches.Enqueue(Batch("old", "b1", Track(1, 10)));
        await _service.GetNextTracksAsync(1);

        _batches.Enqueue("{\"unknownSession\":true,\"sequence\":[]}");
        _batches.Enqueue(Batch("fresh", "b2", Track(5, 50)));
        var songs = await _service.GetNextTracksAsync(1);

        Assert.Equal(["5:50"], songs.Select(s => s.ExternalId!).ToArray());
        Assert.Equal(2, _sessionsCreated);
        // the new session continues after the last track the client got
        var renewed = _requests.Last(r => r.Path == "/rotor/session/new").Body;
        Assert.Equal("1:10", renewed.GetProperty("queue")[0].GetString());
    }

    [Fact]
    public async Task Playback_ReportsStartSkipAndFinish()
    {
        _batches.Enqueue(Batch("sid", "b1", Track(1, 10), Track(2, 20)));
        await _service.GetNextTracksAsync(2);

        await _service.TrackStartedAsync("yandex", "1:10");
        _time.Now += TimeSpan.FromSeconds(5);
        await _service.TrackStartedAsync("yandex", "1:10"); // a range request of the same track
        _time.Now += TimeSpan.FromSeconds(20);
        await _service.TrackStartedAsync("yandex", "2:20"); // left 1 after 25 s
        _time.Now += TimeSpan.FromSeconds(500);
        await _service.TrackFinishedAsync("yandex", "2:20");
        await _service.TrackFinishedAsync("yandex", "2:20"); // a repeated submission

        Assert.Equal(
            [
                ("radioStarted", null, null),
                ("trackStarted", "1:10", null),
                ("skip", "1:10", 25),
                ("trackStarted", "2:20", null),
                ("trackFinished", "2:20", 180)
            ],
            Feedback().ToArray());
        Assert.All(_requests.Where(r => r.Path.EndsWith("/feedback")),
            r => Assert.Equal("/rotor/session/sid/feedback", r.Path));
    }

    [Fact]
    public async Task Playback_OfSongsOutsideTheStationIsNotReported()
    {
        _batches.Enqueue(Batch("sid", "b1", Track(1, 10)));
        await _service.GetNextTracksAsync(1);
        var before = _requests.Count;

        await _service.TrackStartedAsync("yandex", "99:990");
        await _service.TrackFinishedAsync("deezer", "1:10");

        Assert.Equal(before, _requests.Count);
    }

    [Fact]
    public async Task Playback_BeforeAnySessionIsNotReported()
    {
        await _service.TrackStartedAsync("yandex", "1:10");

        Assert.Empty(_requests);
    }
}
