using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.Lyrics;

/// <summary>
/// Fetches synchronized lyrics from an LRCLIB instance (https://lrclib.net).
/// LRCLIB is a free, account-less, community lyrics database. Lookups are matched
/// on artist + title + album + duration and cached in-memory.
/// </summary>
public class LrclibLyricsService : ILyricsService
{
    public const string HttpClientName = "Lyrics";

    private const int DurationToleranceSeconds = 2;
    private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LyricsSettings _settings;
    private readonly ILogger<LrclibLyricsService> _logger;

    private readonly ConcurrentDictionary<string, (SongLyrics? Value, DateTime Expiry)> _cache = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LrclibLyricsService(
        IHttpClientFactory httpClientFactory,
        IOptions<LyricsSettings> settings,
        ILogger<LrclibLyricsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool Enabled => _settings.Enabled;

    public async Task<SongLyrics?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled
            || string.IsNullOrWhiteSpace(song.Title)
            || string.IsNullOrWhiteSpace(song.Artist))
        {
            return null;
        }

        var cacheKey = BuildCacheKey(song);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            return cached.Value;
        }

        SongLyrics? result = null;
        try
        {
            result = await FetchAsync(song, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Lyrics are non-critical: log and treat as a miss (cached briefly to avoid hammering).
            _logger.LogDebug(ex, "Lyrics lookup failed for {Artist} - {Title}", song.Artist, song.Title);
        }

        _cache[cacheKey] = (result, DateTime.UtcNow.Add(result != null ? PositiveCacheTtl : NegativeCacheTtl));
        return result;
    }

    public async Task TryWriteSidecarAsync(string audioFilePath, Song song, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || !_settings.WriteLrcFile || string.IsNullOrEmpty(audioFilePath))
        {
            return;
        }

        try
        {
            var lrcPath = Path.ChangeExtension(audioFilePath, ".lrc");
            if (File.Exists(lrcPath))
            {
                return;
            }

            var lyrics = await GetLyricsAsync(song, cancellationToken);
            if (lyrics is not { HasContent: true })
            {
                return;
            }

            await File.WriteAllTextAsync(lrcPath, LrcFormat.ToLrc(lyrics), cancellationToken);
            _logger.LogInformation("Wrote lyrics sidecar: {Path}", lrcPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write lyrics sidecar for {Path}", audioFilePath);
        }
    }

    private async Task<SongLyrics?> FetchAsync(Song song, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var baseUrl = _settings.LrclibBaseUrl.TrimEnd('/');

        // 1) Exact match via /api/get (most precise: also keyed on album + duration).
        var record = await TryGetExactAsync(client, baseUrl, song, cancellationToken);

        // 2) Fall back to /api/search and pick the closest candidate by duration.
        record ??= await TrySearchAsync(client, baseUrl, song, cancellationToken);

        return record is null ? null : BuildLyrics(song, record.Value);
    }

    private async Task<LrclibRecord?> TryGetExactAsync(
        HttpClient client, string baseUrl, Song song, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/api/get?artist_name={Uri.EscapeDataString(song.Artist)}"
                  + $"&track_name={Uri.EscapeDataString(song.Title)}";
        if (!string.IsNullOrWhiteSpace(song.Album))
        {
            url += $"&album_name={Uri.EscapeDataString(song.Album)}";
        }
        if (song.Duration is > 0)
        {
            url += $"&duration={song.Duration.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadRecord(doc.RootElement);
    }

    private async Task<LrclibRecord?> TrySearchAsync(
        HttpClient client, string baseUrl, Song song, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/api/search?track_name={Uri.EscapeDataString(song.Title)}"
                  + $"&artist_name={Uri.EscapeDataString(song.Artist)}";

        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        LrclibRecord? best = null;
        var bestScore = int.MaxValue;
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var candidate = ReadRecord(element);
            if (candidate is null)
            {
                continue;
            }

            // Prefer a candidate close to the requested duration; among equals, prefer synced.
            var durationDelta = song.Duration is > 0 && candidate.Value.Duration is > 0
                ? Math.Abs(song.Duration.Value - candidate.Value.Duration.Value)
                : 0;
            var score = durationDelta * 2 + (candidate.Value.HasSynced ? 0 : 1);

            if (song.Duration is > 0 && candidate.Value.Duration is > 0
                && durationDelta > DurationToleranceSeconds + 3)
            {
                continue; // too far off to be the same recording
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private SongLyrics? BuildLyrics(Song song, LrclibRecord record)
    {
        var lyrics = new SongLyrics
        {
            DisplayArtist = string.IsNullOrWhiteSpace(record.ArtistName) ? song.Artist : record.ArtistName!,
            DisplayTitle = string.IsNullOrWhiteSpace(record.TrackName) ? song.Title : record.TrackName!,
        };

        if (record.HasSynced)
        {
            lyrics.Synced = true;
            lyrics.Lines = LrcFormat.ParseSynced(record.SyncedLyrics);
        }
        else if (_settings.AllowPlainFallback && !string.IsNullOrWhiteSpace(record.PlainLyrics))
        {
            lyrics.Synced = false;
            lyrics.Lines = LrcFormat.ParsePlain(record.PlainLyrics);
        }

        return lyrics.HasContent ? lyrics : null;
    }

    private static LrclibRecord? ReadRecord(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var record = new LrclibRecord
        {
            TrackName = GetString(element, "trackName"),
            ArtistName = GetString(element, "artistName"),
            SyncedLyrics = GetString(element, "syncedLyrics"),
            PlainLyrics = GetString(element, "plainLyrics"),
        };

        if (element.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
        {
            record.Duration = (int)Math.Round(dur.GetDouble());
        }

        return record;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string BuildCacheKey(Song song)
    {
        var artist = StringNormalizer.CreateComparisonKey(song.Artist);
        var title = StringNormalizer.CreateComparisonKey(song.Title);
        var bucket = song.Duration is > 0 ? song.Duration.Value / 5 : 0; // 5s buckets
        return $"{artist}|{title}|{bucket}";
    }

    private struct LrclibRecord
    {
        public string? TrackName { get; set; }
        public string? ArtistName { get; set; }
        public string? SyncedLyrics { get; set; }
        public string? PlainLyrics { get; set; }
        public int? Duration { get; set; }

        public readonly bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);
    }
}
