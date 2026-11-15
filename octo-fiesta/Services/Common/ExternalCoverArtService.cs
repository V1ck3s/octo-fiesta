using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Cover art of provider artists, albums, songs and playlists, kept in memory so the
/// now-playing screen gets its image at once, even while the track itself is still downloading.
/// The provider's display-size image by default; the large one (a Qobuz original can weigh
/// several MB) only when COVER_ART_SIZE=Large.
/// </summary>
public sealed class ExternalCoverArtService
{
    public sealed record CoverImage(byte[] Data, string ContentType);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);
    private const long CacheBudgetBytes = 100 * 1024 * 1024;

    // Used when no IHttpClientFactory is given (tests, manual construction)
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    private readonly IMusicMetadataService _metadataService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _covers = new();
    private readonly object _trimLock = new();

    // Lazy, so concurrent requests racing to add the same key still start a single download
    private sealed class CacheEntry(Func<Task<CoverImage?>> fetch, DateTime expiry)
    {
        public Lazy<Task<CoverImage?>> Image { get; } = new(fetch);
        public DateTime Expiry { get; } = expiry;
        public long LastAccessTicks = expiry.Ticks;

        public bool IsUsable(DateTime now) => Expiry > now && !(Image.IsValueCreated && Image.Value.IsFaulted);

        public long CachedBytes => Image.IsValueCreated && Image.Value.IsCompletedSuccessfully
            ? Image.Value.Result?.Data.Length ?? 0
            : 0;
    }

    private readonly bool _large;

    public ExternalCoverArtService(IMusicMetadataService metadataService, ILogger<ExternalCoverArtService> logger,
        IHttpClientFactory? httpClientFactory = null, IOptions<SubsonicSettings>? settings = null)
    {
        _large = settings?.Value.CoverArtSize == CoverArtSize.Large;
        _metadataService = metadataService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// The cover for an external id of the given type ("artist", "album" or "song"), or null.
    /// </summary>
    public Task<CoverImage?> GetAsync(string provider, string type, string externalId) =>
        GetCachedAsync($"{provider}|{type}|{externalId}", () => ResolveUrlAsync(provider, type, externalId));

    public Task<CoverImage?> GetPlaylistCoverAsync(string provider, string externalId) =>
        GetCachedAsync($"{provider}|playlist|{externalId}",
            async () => (await _metadataService.GetPlaylistAsync(provider, externalId))?.CoverUrl);

    /// <summary>
    /// Starts fetching a song's cover in the background, so it is ready by the time the
    /// player asks for it, which it usually does right as playback starts.
    /// </summary>
    public void Prefetch(string provider, string songExternalId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GetAsync(provider, "song", songExternalId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cover prefetch failed for {Provider}:{Id}", provider, songExternalId);
            }
        });
    }

    private async Task<CoverImage?> GetCachedAsync(string key, Func<Task<string?>> resolveUrl)
    {
        var now = DateTime.UtcNow;
        var entry = _covers.AddOrUpdate(key,
            _ => new CacheEntry(() => FetchAsync(resolveUrl), now + CacheTtl),
            (_, existing) => existing.IsUsable(now)
                ? existing
                : new CacheEntry(() => FetchAsync(resolveUrl), now + CacheTtl));
        Interlocked.Exchange(ref entry.LastAccessTicks, now.Ticks);

        CoverImage? image;
        try
        {
            image = await entry.Image.Value;
        }
        catch
        {
            _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
            throw;
        }

        if (image == null)
        {
            // Not cached: a cover that failed is asked again next time
            _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
        }
        else
        {
            TrimCache(now);
        }
        return image;
    }

    private async Task<CoverImage?> FetchAsync(Func<Task<string?>> resolveUrl)
    {
        // Off the caller's stack, so the shared task never carries the first caller's state
        await Task.Yield();

        var url = await resolveUrl();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var client = _httpClientFactory?.CreateClient("CoverArtClient") ?? SharedClient.Value;
        using var timeout = new CancellationTokenSource(FetchTimeout);
        try
        {
            using var response = await client.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cover art fetch failed for {Url}: HTTP {Status}", url, (int)response.StatusCode);
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            return new CoverImage(data, contentType);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Cover art fetch timed out after {Seconds}s for {Url}", FetchTimeout.TotalSeconds, url);
            return null;
        }
    }

    private async Task<string?> ResolveUrlAsync(string provider, string type, string externalId)
    {
        switch (type)
        {
            case "artist":
                return (await _metadataService.GetArtistAsync(provider, externalId))?.ImageUrl;

            case "album":
                var album = await _metadataService.GetAlbumAsync(provider, externalId);
                return album == null ? null : Pick(album.CoverArtUrl, album.CoverArtUrlLarge);

            default:
                var song = await _metadataService.GetSongAsync(provider, externalId);
                if (song?.CoverArtUrl != null || song?.CoverArtUrlLarge != null)
                {
                    return Pick(song.CoverArtUrl, song.CoverArtUrlLarge);
                }

                var albumFallback = await _metadataService.GetAlbumAsync(provider, externalId);
                return albumFallback == null ? null : Pick(albumFallback.CoverArtUrl, albumFallback.CoverArtUrlLarge);
        }
    }

    // The configured size, the other one when the provider lacks it
    private string? Pick(string? display, string? large) => _large ? large ?? display : display ?? large;

    // Drops expired covers, then the least recently used ones until the cache fits its byte budget
    private void TrimCache(DateTime now)
    {
        lock (_trimLock)
        {
            foreach (var (key, value) in _covers)
            {
                if (value.Expiry <= now)
                {
                    _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, value));
                }
            }

            var total = _covers.Values.Sum(e => e.CachedBytes);
            if (total <= CacheBudgetBytes)
            {
                return;
            }

            foreach (var (key, value) in _covers.Where(e => e.Value.CachedBytes > 0)
                         .OrderBy(e => Interlocked.Read(ref e.Value.LastAccessTicks)).ToList())
            {
                if (_covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, value)))
                {
                    total -= value.CachedBytes;
                }

                if (total <= CacheBudgetBytes)
                {
                    return;
                }
            }
        }
    }
}
