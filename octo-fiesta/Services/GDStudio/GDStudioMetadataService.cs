using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.GDStudio;

/// <summary>
/// Metadata via the GD Studio API. Upstream only has track search (plus "_album" search that
/// lists tracks of matching albums), so albums/artists are derived from track results and
/// identified by name, and found songs are remembered to serve GetSongAsync.
/// </summary>
public class GDStudioMetadataService : IMusicMetadataService
{
    public const string ProviderName = "gdstudio";

    private readonly HttpClient _http;
    private readonly ILogger<GDStudioMetadataService> _logger;
    private readonly GDStudioSettings _s;
    private string _source => _s.Source;

    // ponytail: unbounded in-memory map, add eviction if search volume ever makes it matter
    private readonly ConcurrentDictionary<string, Song> _songs = new();
    private readonly ConcurrentDictionary<string, string> _pics = new();

    public GDStudioMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<GDStudioSettings> settings,
        ILogger<GDStudioMetadataService> logger)
    {
        _http = httpClientFactory.CreateClient(GDStudioHttpClientConfiguration.ClientName);
        _logger = logger;
        _s = settings.Value;
    }

    // Albums and artists have no ids upstream: the id is the base64url-encoded name.
    private const string AlbumPrefix = "ext-gdstudio-album-";
    private const string ArtistPrefix = "ext-gdstudio-artist-";

    private static string Enc(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Dec(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
    }

    private async Task<List<GDStudioTrack>> QueryAsync(string source, string name, int count)
    {
        try
        {
            var url = _s.Url($"types=search&source={Uri.EscapeDataString(source)}"
                      + $"&name={Uri.EscapeDataString(name)}&count={count}");
            var results = await _http.GetFromJsonAsync<List<GDStudioTrack>>(url) ?? [];
            return results.Where(t => !string.IsNullOrEmpty(t.Id)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GDStudio search failed for '{Query}'", name);
            return [];
        }
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
        => (await QueryAsync(_source, query, limit)).Select(ToSong).ToList();

    // `<source>_album` returns the tracks of albums matching the keyword.
    private async Task<List<Album>> AlbumsFromAsync(string source, string name, int count, Func<GDStudioTrack, bool>? filter = null)
    {
        var tracks = (await QueryAsync(source, name, count)).Where(t => filter?.Invoke(t) ?? true).ToList();
        return tracks.GroupBy(t => t.Album).Where(g => g.Key != "").Select(g => new Album
        {
            Id = AlbumPrefix + Enc(g.Key),
            Title = g.Key,
            Artist = g.First().Artist.FirstOrDefault() ?? "",
            ArtistId = g.First().Artist.Count > 0 ? ArtistPrefix + Enc(g.First().Artist[0]) : null,
            SongCount = g.Count(),
            ExternalProvider = ProviderName,
            ExternalId = Enc(g.Key)
        }).ToList();
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
        => (await AlbumsFromAsync(_source + "_album", query, 50)).Take(limit).ToList();

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;
        var name = Dec(externalId);
        var tracks = (await QueryAsync(_source + "_album", name, 100)).Where(t => t.Album == name).ToList();
        if (tracks.Count == 0) return null;
        var songs = tracks.Select(ToSong).ToList();
        for (var i = 0; i < songs.Count; i++) { songs[i].Track = i + 1; songs[i].TotalTracks = songs.Count; }
        return new Album
        {
            Id = AlbumPrefix + externalId,
            Title = name,
            Artist = songs[0].AlbumArtist ?? "",
            ArtistId = songs[0].ArtistId,
            SongCount = songs.Count,
            ExternalProvider = ProviderName,
            ExternalId = externalId,
            Songs = songs
        };
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
        => (await QueryAsync(_source, query, 50)).SelectMany(t => t.Artist).Distinct().Take(limit).Select(ToArtist).ToList();

    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
        => Task.FromResult(externalProvider == ProviderName ? ToArtist(Dec(externalId)) : null);

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return [];
        var name = Dec(externalId);
        return await AlbumsFromAsync(_source, name, 50, t => t.Artist.Contains(name));
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var songs = (await QueryAsync(_source, query, Math.Max(songLimit, artistLimit))).ToList();
        return new SearchResult
        {
            Songs = songs.Take(songLimit).Select(ToSong).ToList(),
            Artists = songs.SelectMany(t => t.Artist).Distinct().Take(artistLimit).Select(ToArtist).ToList(),
            Albums = await SearchAlbumsAsync(query, albumLimit)
        };
    }

    // No get-by-id upstream ("url" only returns the file link), so serve what we've seen in
    // searches/albums. After a restart an unseen id falls back to a bare song (title = id).
    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;
        var song = _songs.GetOrAdd(externalId, id => new Song
        {
            Title = id, ExternalProvider = ProviderName, ExternalId = id
        });
        if (song.CoverArtUrlLarge is null && _pics.TryGetValue(externalId, out var pic))
        {
            song.CoverArtUrlLarge = song.CoverArtUrl = await ResolvePicAsync(pic);
        }
        return song;
    }

    public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<ExternalPlaylist>());
    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId) => Task.FromResult<ExternalPlaylist?>(null);
    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId) => Task.FromResult(new List<Song>());

    private async Task<string?> ResolvePicAsync(string picId)
    {
        if (picId.StartsWith("//")) return "https:" + picId;
        if (picId.StartsWith("http")) return picId;
        try
        {
            var r = await _http.GetFromJsonAsync<GDStudioPic>(
                _s.Url($"types=pic&source={Uri.EscapeDataString(_source)}&id={Uri.EscapeDataString(picId)}&size=500"));
            return string.IsNullOrEmpty(r?.Url) ? null : r.Url;
        }
        catch { return null; }
    }

    private static Artist ToArtist(string name) => new()
    {
        Id = ArtistPrefix + Enc(name), Name = name, ExternalProvider = ProviderName, ExternalId = Enc(name)
    };

    public async Task<string?> GetLyricsAsync(string trackId)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<GDStudioLyric>(
                _s.Url($"types=lyric&source={Uri.EscapeDataString(_source)}&id={Uri.EscapeDataString(trackId)}"));
            return string.IsNullOrWhiteSpace(r?.Lyric) ? null : r.Lyric;
        }
        catch { return null; }
    }

    private Song ToSong(GDStudioTrack t)
    {
        var artists = t.Artist.Select(ToArtist).ToList();
        var song = new Song
        {
            Title = t.Name,
            Artist = string.Join(", ", t.Artist),
            Artists = artists,
            ArtistId = artists.FirstOrDefault()?.Id,
            Album = t.Album,
            AlbumId = t.Album == "" ? null : AlbumPrefix + Enc(t.Album),
            AlbumArtist = t.Artist.FirstOrDefault(),
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = t.Id
        };
        _songs[t.Id] = song;
        if (!string.IsNullOrEmpty(t.PicId)) _pics[t.Id] = t.PicId;
        return song;
    }

    private record GDStudioTrack(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("artist")] List<string> Artist,
        [property: JsonPropertyName("album")] string Album,
        [property: JsonPropertyName("pic_id")] string? PicId);

    private record GDStudioPic([property: JsonPropertyName("url")] string? Url);

    private record GDStudioLyric([property: JsonPropertyName("lyric")] string? Lyric);
}
