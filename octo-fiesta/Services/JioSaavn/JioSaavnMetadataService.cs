using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.JioSaavn;

public class JioSaavnMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<JioSaavnMetadataService> _logger;
    private const string SearchBaseUrl = "https://rtmx.vercel.app/api";

    public JioSaavnMetadataService(
        IHttpClientFactory httpClientFactory,
        ILogger<JioSaavnMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
    }

    public async Task<string?> GetSongDownloadUrlAsync(string songToken)
    {
        try
        {
            var url = $"https://sda.rhythmax.workers.dev/song?url=https%3A%2F%2Fwww.jiosaavn.com%2Fsong%2Fsong%2F{songToken}";
            using var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var encryptedMediaUrl = doc.RootElement.GetProperty("more_info").GetProperty("encrypted_media_url").GetString();
            if (string.IsNullOrEmpty(encryptedMediaUrl)) return null;

            var decryptedUrl = JioSaavnCrypto.Decrypt(encryptedMediaUrl);
            
            // Swap quality to 320.mp4
            return decryptedUrl.Replace("_96.mp4", "_320.mp4").Replace("_160.mp4", "_320.mp4");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get download URL for song: {Token}", songToken);
            return null;
        }
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        try
        {
            var url = $"{SearchBaseUrl}/songs?q={Uri.EscapeDataString(query)}";
            using var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return new List<Song>();

            var songs = new List<Song>();
            foreach (var item in results.EnumerateArray().Take(limit))
            {
                songs.Add(MapJioSaavnTrackToSong(item));
            }
            return songs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search songs for query: {Query}", query);
            return new List<Song>();
        }
    }

    private static Song MapJioSaavnTrackToSong(JsonElement track)
    {
        var externalId = track.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
        var title = track.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
        
        var moreInfo = track.GetProperty("more_info");
        var album = moreInfo.TryGetProperty("album", out var a) ? a.GetString() : "";
        var albumId = moreInfo.TryGetProperty("album_token", out var at) ? $"ext-jiosaavn-album-{at.GetString()}" : null;
        
        var yearStr = track.TryGetProperty("year", out var y) ? y.GetString() : null;
        int? year = int.TryParse(yearStr, out var parsedYear) ? parsedYear : null;
        
        // JioSaavn usually returns 150x150 images. We can manipulate the URL to get 500x500.
        var coverUrl = track.TryGetProperty("image", out var i) ? i.GetString()?.Replace("150x150", "500x500") : null;
        
        string artistName = "";
        if (moreInfo.TryGetProperty("artists", out var artistsNode) && 
            artistsNode.TryGetProperty("primary", out var primaryNode) &&
            primaryNode.ValueKind == JsonValueKind.Array &&
            primaryNode.GetArrayLength() > 0)
        {
            artistName = primaryNode[0].GetProperty("name").GetString() ?? "";
        }

        return new Song
        {
            Title = title,
            Artist = artistName,
            Artists = !string.IsNullOrEmpty(artistName) 
                ? new List<Artist> { new Artist { Id = "", Name = artistName, IsLocal = false, ExternalProvider = "jiosaavn" } } 
                : new List<Artist>(),
            Album = album ?? "",
            AlbumId = albumId,
            Year = year,
            CoverArtUrl = coverUrl,
            CoverArtUrlLarge = coverUrl,
            IsLocal = false,
            ExternalProvider = "jiosaavn",
            ExternalId = externalId
        };
    }

    // --- IMusicMetadataService Contract Stubs ---
    public Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20) => Task.FromResult(new List<Album>());
    public Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20) => Task.FromResult(new List<Artist>());
    public Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20) => Task.FromResult(new SearchResult());
    public Task<Song?> GetSongAsync(string externalProvider, string externalId) => Task.FromResult<Song?>(null);
    public Task<Album?> GetAlbumAsync(string externalProvider, string externalId) => Task.FromResult<Album?>(null);
    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId) => Task.FromResult<Artist?>(null);
    public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId) => Task.FromResult(new List<Album>());
    public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<ExternalPlaylist>());
    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId) => Task.FromResult<ExternalPlaylist?>(null);
    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId) => Task.FromResult(new List<Song>());
}
