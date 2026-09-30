using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.Composite;

/// <summary>
/// Fans searches out to every configured provider and merges the results; lookups by id are
/// routed to the provider named in the id. A provider that throws during search is skipped.
/// </summary>
public class CompositeMetadataService : IMusicMetadataService
{
    public IReadOnlyList<(string Key, IMusicMetadataService Service)> Providers { get; }
    private readonly ILogger<CompositeMetadataService> _logger;

    public CompositeMetadataService(
        IReadOnlyList<(string Key, IMusicMetadataService Service)> providers,
        ILogger<CompositeMetadataService> logger)
    {
        Providers = providers;
        _logger = logger;
    }

    private IMusicMetadataService? For(string provider)
        => Providers.FirstOrDefault(p => p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Service;

    private async Task<List<List<T>>> FanOut<T>(Func<IMusicMetadataService, Task<List<T>>> call)
    {
        var tasks = Providers.Select(async p =>
        {
            try { return await call(p.Service); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} failed, skipping it for this request", p.Key);
                return new List<T>();
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    // Round-robin so every provider is represented within the limit.
    private static List<T> Interleave<T>(List<List<T>> lists, int limit)
    {
        var result = new List<T>();
        for (var i = 0; result.Count < limit && lists.Any(l => i < l.Count); i++)
            foreach (var l in lists)
                if (i < l.Count && result.Count < limit) result.Add(l[i]);
        return result;
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
        => Interleave(await FanOut(s => s.SearchSongsAsync(query, limit)), limit);

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
        => Interleave(await FanOut(s => s.SearchAlbumsAsync(query, limit)), limit);

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
        => Interleave(await FanOut(s => s.SearchArtistsAsync(query, limit)), limit);

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
        => Interleave(await FanOut(s => s.SearchPlaylistsAsync(query, limit)), limit);

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var tasks = Providers.Select(async p =>
        {
            try { return await p.Service.SearchAllAsync(query, songLimit, albumLimit, artistLimit); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} failed, skipping it for this search", p.Key);
                return new SearchResult();
            }
        });
        var all = await Task.WhenAll(tasks);
        return new SearchResult
        {
            Songs = Interleave(all.Select(r => r.Songs).ToList(), songLimit),
            Albums = Interleave(all.Select(r => r.Albums).ToList(), albumLimit),
            Artists = Interleave(all.Select(r => r.Artists).ToList(), artistLimit)
        };
    }

    public Task<Song?> GetSongAsync(string externalProvider, string externalId)
        => For(externalProvider)?.GetSongAsync(externalProvider, externalId) ?? Task.FromResult<Song?>(null);

    public Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
        => For(externalProvider)?.GetAlbumAsync(externalProvider, externalId) ?? Task.FromResult<Album?>(null);

    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
        => For(externalProvider)?.GetArtistAsync(externalProvider, externalId) ?? Task.FromResult<Artist?>(null);

    public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
        => For(externalProvider)?.GetArtistAlbumsAsync(externalProvider, externalId) ?? Task.FromResult(new List<Album>());

    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
        => For(externalProvider)?.GetPlaylistAsync(externalProvider, externalId) ?? Task.FromResult<ExternalPlaylist?>(null);

    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
        => For(externalProvider)?.GetPlaylistTracksAsync(externalProvider, externalId) ?? Task.FromResult(new List<Song>());
}
