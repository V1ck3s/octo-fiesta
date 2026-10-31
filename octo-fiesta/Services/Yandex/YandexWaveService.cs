using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Models.Yandex;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.Yandex;

/// <summary>
/// Yandex Music "My Wave" (rotor station user:onyourwave) as a playlist.
/// </summary>
/// <remarks>
/// A rotor session hands out the station in small batches (4-5 tracks) and learns from
/// playback feedback. Every read of the playlist continues the same session, so the client
/// gets fresh tracks each time it opens it. Streams and scrobbles of station tracks are
/// reported back: trackStarted, trackFinished, and skip when a track is left unfinished
/// for the next one. An expired session is replaced transparently.
/// </remarks>
public class YandexWaveService : IStationService
{
    public const string ProviderName = "yandex";
    public const string WaveExternalId = "wave";
    private const string WaveSeed = "user:onyourwave";
    private const string FeedbackFrom = "octo-fiesta";
    // batches hold 4-5 tracks; a read stops after this many requests per requested track pair
    private const int MinBatchRequests = 10;
    // station tracks remembered for feedback and for not handing them out twice, oldest dropped first
    private const int MaxRememberedTracks = 500;
    // tracks already handed out that a batch request tells Yandex about, so it does not repeat them
    private const int MaxQueueTracks = 50;
    private const int CoverSize = 600;
    // the My Wave artwork as /rotor/station/user:onyourwave/info returned it on 2026-10-31,
    // used until (or unless) the station info gives the current one
    private const string KnownCoverUri = "avatars.yandex.net/get-music-misc/70850/img.5ec4fb88d7b1560bc894e184/%%";
    private static readonly TimeSpan CoverRetryInterval = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<YandexWaveService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _includeUnavailable;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _sessionId;
    // handed-out tracks, oldest first: Yandex only skips what the request queue lists
    private readonly List<string> _handedOut = new();
    private readonly HashSet<string> _handedOutSet = new();
    private readonly Dictionary<string, StationTrack> _tracks = new();
    private readonly Queue<string> _trackOrder = new();
    private string? _currentKey;
    // whether the station info has given the current artwork (else the known one is used)
    private bool _coverFromStation;
    private DateTimeOffset _coverAttemptedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _currentStarted;
    private bool _currentFinished;

    public YandexWaveService(
        IHttpClientFactory httpClientFactory,
        IOptions<YandexSettings> yandexSettings,
        ILogger<YandexWaveService> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClientFactory.CreateClient("Yandex");
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _includeUnavailable = yandexSettings.Value.IncludeUnavailable;
        TracksPerRead = Math.Clamp(yandexSettings.Value.WaveTracks, 1, 200);

        var russian = string.Equals(yandexSettings.Value.Language, "ru", StringComparison.OrdinalIgnoreCase);
        StationPlaylist = new ExternalPlaylist
        {
            Id = PlaylistIdHelper.CreatePlaylistId(ProviderName, WaveExternalId),
            Name = russian ? "Моя волна" : "My Wave",
            Description = russian
                ? "Персональная радиостанция Яндекс Музыки, новые треки при каждом открытии"
                : "Yandex Music personal radio, new tracks every time it is opened",
            CuratorName = russian ? "Яндекс Музыка" : "Yandex Music",
            Provider = ProviderName,
            ExternalId = WaveExternalId,
            TrackCount = TracksPerRead,
            CoverUrl = YandexMetadataService.MakeCoverUri(KnownCoverUri, CoverSize)
        };
    }

    /// <summary>
    /// Number of tracks one read of the playlist returns.
    /// </summary>
    public int TracksPerRead { get; }

    public ExternalPlaylist StationPlaylist { get; }

    /// <summary>
    /// The station playlist with its cover: Yandex's own My Wave artwork, looked up once (retried every
    /// 10 minutes while it fails); until then the artwork known at the time of writing.
    /// </summary>
    public async Task<ExternalPlaylist> GetStationPlaylistAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (!_coverFromStation && now - _coverAttemptedAt >= CoverRetryInterval)
        {
            _coverAttemptedAt = now;
            var info = await GetAsync<List<YandexRotorStationInfo>>(
                $"/rotor/station/{WaveSeed}/info", cancellationToken);
            var cover = YandexMetadataService.MakeCoverUri(info?.FirstOrDefault()?.Station?.FullImageUrl, CoverSize);
            if (cover != null)
            {
                StationPlaylist.CoverUrl = cover;
                _coverFromStation = true;
            }
        }

        return StationPlaylist;
    }

    /// <summary>
    /// Continues the station (or starts it) and returns the next tracks.
    /// </summary>
    public async Task<List<Song>> GetNextTracksAsync(int count, CancellationToken cancellationToken = default)
    {
        var songs = new List<Song>();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var maxRequests = Math.Max(MinBatchRequests, count / 2);
            for (var request = 0; request < maxRequests && songs.Count < count; request++)
            {
                var batch = await NextBatchAsync(cancellationToken);
                if (batch == null)
                {
                    break;
                }

                foreach (var track in batch.Sequence.Select(item => item.Track))
                {
                    if (track == null || track.Error != null || (track.Available == false && !_includeUnavailable))
                    {
                        continue;
                    }

                    var song = YandexMetadataService.MapYandexTrackToSong(track);
                    if (string.IsNullOrEmpty(song.ExternalId) || !HandOut(song.ExternalId))
                    {
                        // batches fetched back to back overlap; a track goes to the client once
                        continue;
                    }

                    Remember(song.ExternalId, batch.BatchId, song.Duration);
                    songs.Add(song);
                }

                if (batch.Terminated == true)
                {
                    // the station ended this session; the next read starts a new one
                    _sessionId = null;
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        return songs.Take(count).ToList();
    }

    public void TrackStarted(string provider, string externalId)
    {
        _ = Task.Run(() => TrackStartedAsync(provider, externalId));
    }

    public void TrackFinished(string provider, string externalId)
    {
        _ = Task.Run(() => TrackFinishedAsync(provider, externalId));
    }

    /// <summary>
    /// Awaitable form of <see cref="TrackStarted"/>. A different station track that was started
    /// and not finished is reported as skipped first.
    /// </summary>
    internal async Task TrackStartedAsync(string provider, string externalId)
    {
        try
        {
            await _lock.WaitAsync();
            try
            {
                if (provider != ProviderName || _sessionId == null || !_tracks.ContainsKey(externalId))
                {
                    return;
                }

                if (_currentKey == externalId && !_currentFinished)
                {
                    // range requests and seeks of the playing track
                    return;
                }

                var now = _timeProvider.GetUtcNow();
                if (_currentKey != null && !_currentFinished)
                {
                    await SendFeedbackAsync("skip", _currentKey, PlayedSeconds(_currentKey, now));
                }

                await SendFeedbackAsync("trackStarted", externalId, null);
                _currentKey = externalId;
                _currentStarted = now;
                _currentFinished = false;
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "My Wave: could not report the start of {TrackId}", externalId);
        }
    }

    /// <summary>
    /// Awaitable form of <see cref="TrackFinished"/>.
    /// </summary>
    internal async Task TrackFinishedAsync(string provider, string externalId)
    {
        try
        {
            await _lock.WaitAsync();
            try
            {
                if (provider != ProviderName || _sessionId == null || !_tracks.TryGetValue(externalId, out var track))
                {
                    return;
                }

                if (_currentKey == externalId && _currentFinished)
                {
                    return;
                }

                var played = _currentKey == externalId
                    ? PlayedSeconds(externalId, _timeProvider.GetUtcNow())
                    : track.DurationSeconds ?? 0;

                await SendFeedbackAsync("trackFinished", externalId, played);
                _currentKey = externalId;
                _currentFinished = true;
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "My Wave: could not report the end of {TrackId}", externalId);
        }
    }

    private async Task<YandexRotorSession?> NextBatchAsync(CancellationToken cancellationToken)
    {
        if (_sessionId != null)
        {
            var next = await PostAsync<YandexRotorSession>(
                $"/rotor/session/{Uri.EscapeDataString(_sessionId)}/tracks",
                new { queue = RecentQueue() },
                cancellationToken);

            if (next?.UnknownSession != true)
            {
                return next;
            }

            _logger.LogInformation("My Wave: session expired, starting a new one");
            _sessionId = null;
        }

        var session = await PostAsync<YandexRotorSession>(
            "/rotor/session/new",
            new
            {
                seeds = new[] { WaveSeed },
                queue = RecentQueue(),
                includeTracksInResponse = true,
                includeWaveModel = true,
                interactive = true
            },
            cancellationToken);

        if (string.IsNullOrEmpty(session?.RadioSessionId))
        {
            return null;
        }

        _sessionId = session.RadioSessionId;
        _currentKey = null;
        _currentFinished = false;
        await SendFeedbackAsync("radioStarted", null, null, session.BatchId);
        return session;
    }

    private string[] RecentQueue() =>
        _handedOut.Skip(Math.Max(0, _handedOut.Count - MaxQueueTracks)).ToArray();

    private bool HandOut(string key)
    {
        if (!_handedOutSet.Add(key))
        {
            return false;
        }

        _handedOut.Add(key);
        if (_handedOut.Count > MaxRememberedTracks)
        {
            _handedOutSet.Remove(_handedOut[0]);
            _handedOut.RemoveAt(0);
        }

        return true;
    }

    private void Remember(string key, string? batchId, int? durationSeconds)
    {
        if (!_tracks.ContainsKey(key))
        {
            _trackOrder.Enqueue(key);
            while (_trackOrder.Count > MaxRememberedTracks)
            {
                _tracks.Remove(_trackOrder.Dequeue());
            }
        }

        _tracks[key] = new StationTrack(batchId, durationSeconds);
    }

    private int PlayedSeconds(string key, DateTimeOffset now)
    {
        var elapsed = (int)Math.Max(0, (now - _currentStarted).TotalSeconds);
        var duration = _tracks.TryGetValue(key, out var track) ? track.DurationSeconds : null;
        return duration is > 0 ? Math.Min(elapsed, duration.Value) : elapsed;
    }

    private async Task SendFeedbackAsync(string type, string? trackKey, int? playedSeconds, string? batchId = null)
    {
        if (_sessionId == null)
        {
            return;
        }

        batchId ??= trackKey != null && _tracks.TryGetValue(trackKey, out var track) ? track.BatchId : null;
        var timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

        object body = type == "radioStarted"
            ? new { @event = new { type, timestamp }, from = FeedbackFrom, batchId }
            : new { @event = new { type, timestamp, trackId = trackKey, totalPlayedSeconds = playedSeconds }, batchId };

        var result = await PostAsync<object>(
            $"/rotor/session/{Uri.EscapeDataString(_sessionId)}/feedback", body, CancellationToken.None);
        if (result == null)
        {
            _logger.LogDebug("My Wave: feedback {Type} for {TrackId} was not accepted", type, trackKey);
        }
    }

    private Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken) where T : class =>
        SendAsync<T>(() => _httpClient.GetAsync(url, cancellationToken), url, cancellationToken);

    private Task<T?> PostAsync<T>(string url, object body, CancellationToken cancellationToken) where T : class =>
        SendAsync<T>(() => _httpClient.PostAsJsonAsync(url, body, JsonOptions, cancellationToken), url, cancellationToken);

    private async Task<T?> SendAsync<T>(
        Func<Task<HttpResponseMessage>> send, string url, CancellationToken cancellationToken) where T : class
    {
        try
        {
            using var response = await send();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Yandex API returned status code {StatusCode} for {Url}", response.StatusCode, url);
                return null;
            }

            var parsed = await response.Content.ReadFromJsonAsync<YandexResponse<T>>(JsonOptions, cancellationToken);
            if (parsed?.Error != null)
            {
                _logger.LogWarning("Yandex API returned an error ({ErrorName}) for {Url}", parsed.Error.Name, url);
                return null;
            }

            return parsed?.Result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Yandex request to {Url} failed", url);
            return null;
        }
    }

    private sealed record StationTrack(string? BatchId, int? DurationSeconds);
}
