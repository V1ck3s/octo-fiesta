using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.JioSaavn;

public sealed class JioSaavnMetadataService : IMusicMetadataService
{
    private const string ProviderName = "jiosaavn";

    private readonly JioSaavnApiClient _apiClient;
    private readonly ILogger<JioSaavnMetadataService> _logger;

    public JioSaavnMetadataService(
        JioSaavnApiClient apiClient,
        ILogger<JioSaavnMetadataService> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<string?> GetSongDownloadUrlAsync(string externalId)
    {
        try
        {
            return await _apiClient.ResolveQualityMediaUrlAsyncByExternalId(
                externalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to resolve JioSaavn download URL for external ID {ExternalId}",
                externalId);

            return null;
        }
    }

    public async Task<List<Song>> SearchSongsAsync(
        string query,
        int limit = 20)
    {
        _logger.LogInformation("JioSaavn SearchSongsAsync: {Query}", query);
        try
        {
            var results = await _apiClient.SearchSongsAsync(query, limit);

            return results
                .Where(song => !string.IsNullOrWhiteSpace(song.PermaUrl))
                .Select(song => MapJioSaavnTrackToSong(song))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to search JioSaavn songs for query {Query}",
                query);

            return [];
        }
    }

    public async Task<SearchResult> SearchAllAsync(
        string query,
        int songLimit = 20,
        int albumLimit = 20,
        int artistLimit = 20)
    {
        _logger.LogInformation("JioSaavn SearchAllAsync: {Query}", query);
        var songs = await SearchSongsAsync(query, songLimit);

        return new SearchResult
        {
            Songs = songs,
            Albums = [],
            Artists = []
        };
    }

    public async Task<Song?> GetSongAsync(
        string externalProvider,
        string externalId)
    {
        if (!externalProvider.Equals(
                ProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var details =
                await _apiClient.GetSongDetailsByExternalIdAsync(externalId);

            return details is null
                ? null
                : MapJioSaavnTrackToSong(details, externalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load JioSaavn song {ExternalId}",
                externalId);

            return null;
        }
    }

    private static Song MapJioSaavnTrackToSong(
        JioSaavnApiSong track,
        string? existingExternalId = null)
    {
        var info = track.MoreInfo;

        var externalId = existingExternalId;
        if (string.IsNullOrWhiteSpace(externalId) &&
            !string.IsNullOrWhiteSpace(track.PermaUrl))
        {
            externalId =
                JioSaavnApiClient.EncodePermaUrlExternalId(track.PermaUrl);
        }

        var artists = MapArtists(info?.Artists?.Primary);
        var featuredArtists = MapArtistNames(info?.Artists?.Featured);

        var artistName = artists.Count > 0
            ? string.Join(", ", artists.Select(artist => artist.Name))
            : ExtractArtistFromSubtitle(track.Subtitle);

        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(artistName))
        {
            artists.Add(new Artist
            {
                Id = string.Empty,
                Name = artistName,
                IsLocal = false,
                ExternalProvider = ProviderName
            });
        }

        var albumId = !string.IsNullOrWhiteSpace(info?.AlbumToken)
            ? $"ext-jiosaavn-album-{info.AlbumToken}"
            : null;

        var coverUrl = UpgradeCoverUrl(track.Image);

        return new Song
        {
            Title = track.Title ?? string.Empty,
            Artist = artistName,
            // JioSaavn lists the lead artist first in "primary" on every track of an album,
            // even when featured collaborators differ per track (e.g. "Villian" + varying
            // features). Using the full joined artistName for folder pathing would split one
            // album into a folder per collaborator combination, so pin AlbumArtist to the lead.
            AlbumArtist = artists.Count > 0 ? artists[0].Name : artistName,
            Artists = artists,
            Contributors = featuredArtists,
            Album = info?.Album ?? string.Empty,
            AlbumId = albumId,
            Duration = ParseInt(info?.Duration),
            Year = ParseInt(track.Year),
            ReleaseDate = info?.ReleaseDate,
            Label = info?.Label,
            Copyright = info?.CopyrightText,
            Genre = track.Language,
            CoverArtUrl = coverUrl,
            CoverArtUrlLarge = coverUrl,
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = externalId
        };
    }

    private static List<Artist> MapArtists(
        IEnumerable<JioSaavnApiArtist>? source)
    {
        if (source is null)
        {
            return [];
        }

        return source
            .Where(artist => !string.IsNullOrWhiteSpace(artist.Name))
            .Select(artist => new Artist
            {
                Id = artist.Id ?? string.Empty,
                Name = artist.Name!,
                ImageUrl = artist.Image,
                IsLocal = false,
                ExternalProvider = ProviderName
            })
            .ToList();
    }

    private static List<string> MapArtistNames(
        IEnumerable<JioSaavnApiArtist>? source)
    {
        if (source is null)
        {
            return [];
        }

        return source
            .Select(artist => artist.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
    }

    private static string ExtractArtistFromSubtitle(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return string.Empty;
        }

        var separatorIndex = subtitle.IndexOf(" - ", StringComparison.Ordinal);
        return separatorIndex > 0
            ? subtitle[..separatorIndex].Trim()
            : subtitle.Trim();
    }

    private static string? UpgradeCoverUrl(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        return image
            .Replace("50x50", "500x500", StringComparison.OrdinalIgnoreCase)
            .Replace("150x150", "500x500", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    public Task<List<Album>> SearchAlbumsAsync(
        string query,
        int limit = 20) =>
        Task.FromResult(new List<Album>());

    public Task<List<Artist>> SearchArtistsAsync(
        string query,
        int limit = 20) =>
        Task.FromResult(new List<Artist>());

    public Task<Album?> GetAlbumAsync(
        string externalProvider,
        string externalId) =>
        Task.FromResult<Album?>(null);

    public Task<Artist?> GetArtistAsync(
        string externalProvider,
        string externalId) =>
        Task.FromResult<Artist?>(null);

    public Task<List<Album>> GetArtistAlbumsAsync(
        string externalProvider,
        string externalId) =>
        Task.FromResult(new List<Album>());

    public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(
        string query,
        int limit = 20) =>
        Task.FromResult(new List<ExternalPlaylist>());

    public Task<ExternalPlaylist?> GetPlaylistAsync(
        string externalProvider,
        string externalId) =>
        Task.FromResult<ExternalPlaylist?>(null);

    public Task<List<Song>> GetPlaylistTracksAsync(
        string externalProvider,
        string externalId) =>
        Task.FromResult(new List<Song>());
}
