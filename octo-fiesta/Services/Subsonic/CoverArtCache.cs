using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace octo_fiesta.Services.Subsonic;

public interface ICoverArtCache
{
    Task<CoverArtPayload> GetOrCreateAsync(
        string key,
        Func<CancellationToken, Task<CoverArtPayload>> factory,
        CancellationToken cancellationToken = default);

    void Remove(string key);
}

public sealed record CoverArtPayload(byte[] Bytes, string ContentType);

public sealed class CoverArtCache : ICoverArtCache
{
    private static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(6),
        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(3),
        Size = 1
    };

    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, Lazy<Task<CoverArtPayload>>> _inFlight = new();

    public CoverArtCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    public async Task<CoverArtPayload> GetOrCreateAsync(
        string key,
        Func<CancellationToken, Task<CoverArtPayload>> factory,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out CoverArtPayload? cached) && cached != null)
        {
            return cached;
        }

        var lazy = _inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<CoverArtPayload>>(() => factory(cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var payload = await lazy.Value;
            _cache.Set(key, payload, CacheOptions);
            return payload;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
        _inFlight.TryRemove(key, out _);
    }
}
