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
    private const int MaxCachedCovers = 256;

    // Used when no IHttpClientFactory is given (tests, manual construction)
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    private readonly IMusicMetadataService _metadataService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _covers = new();

    private sealed record CacheEntry(Task<CoverImage?> Image, DateTime Expiry);

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
        TrimCache(now);

        var entry = _covers.AddOrUpdate(key,
            _ => new CacheEntry(FetchAsync(resolveUrl), now + CacheTtl),
            (_, existing) => existing.Expiry > now && !existing.Image.IsFaulted
                ? existing
                : new CacheEntry(FetchAsync(resolveUrl), now + CacheTtl));

        CoverImage? image;
        try
        {
            image = await entry.Image;
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
        using var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cover art fetch failed for {Url}: HTTP {Status}", url, (int)response.StatusCode);
            return null;
        }

        var data = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
        return new CoverImage(data, contentType);
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

    private void TrimCache(DateTime now)
    {
        if (_covers.Count < MaxCachedCovers)
        {
            return;
        }

        foreach (var (key, value) in _covers)
        {
            if (value.Expiry <= now)
            {
                _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, value));
            }
        }

        // Still full: drop the oldest half
        if (_covers.Count >= MaxCachedCovers)
        {
            foreach (var (key, value) in _covers.OrderBy(e => e.Value.Expiry).Take(MaxCachedCovers / 2).ToList())
            {
                _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, value));
            }
        }
    }
}
