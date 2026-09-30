using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.Subsonic;

public interface IExternalCoverArtService
{
    Task<CoverArtPayload?> ResolveAsync(
        string id,
        (bool isExternal, string? provider, string? type, string? externalId) parsedExternalId,
        int? requestedSize,
        CancellationToken cancellationToken);

    Task MarkAlbumDownloadStartedAsync(string provider, string externalId);
}

public sealed class ExternalCoverArtService : IExternalCoverArtService
{
    public const string HttpClientName = "cover-art";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICoverArtTransformer _coverArtTransformer;
    private readonly ICoverArtCache _coverArtCache;
    private readonly IExternalAlbumAvailabilityService _externalAlbumAvailabilityService;
    private readonly ExternalCoverSettings _settings;
    private readonly IMusicMetadataService _metadataService;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly ILogger<ExternalCoverArtService> _logger;

    public ExternalCoverArtService(
        IHttpClientFactory httpClientFactory,
        ICoverArtTransformer coverArtTransformer,
        ICoverArtCache coverArtCache,
        IExternalAlbumAvailabilityService externalAlbumAvailabilityService,
        IOptions<ExternalCoverSettings> settings,
        IMusicMetadataService metadataService,
        ILocalLibraryService localLibraryService,
        ILogger<ExternalCoverArtService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _coverArtTransformer = coverArtTransformer;
        _coverArtCache = coverArtCache;
        _externalAlbumAvailabilityService = externalAlbumAvailabilityService;
        _settings = settings.Value;
        _metadataService = metadataService;
        _localLibraryService = localLibraryService;
        _logger = logger;
    }

    public async Task<CoverArtPayload?> ResolveAsync(
        string id,
        (bool isExternal, string? provider, string? type, string? externalId) parsedExternalId,
        int? requestedSize,
        CancellationToken cancellationToken)
    {
        var payload = await ResolveAndFetchCoverArtAsync(id, requestedSize, cancellationToken);
        if (payload == null)
        {
            return null;
        }

        var badgeIdentity = await GetExternalCoverBadgeIdentityAsync(parsedExternalId);
        if (badgeIdentity == null)
        {
            return payload;
        }

        var transformKey = CreateCoverCacheKey(
            $"external-cover-v7-{_settings.GetCacheKeySegment()}",
            badgeIdentity.Provider,
            badgeIdentity.Type,
            badgeIdentity.ExternalId,
            requestedSize);
        var sourcePayload = payload;
        return await _coverArtCache.GetOrCreateAsync(
            transformKey,
            async token =>
            {
                var transformed = await _coverArtTransformer.ApplyExternalTreatmentAsync(sourcePayload.Bytes, sourcePayload.ContentType, token);
                return new CoverArtPayload(transformed.Bytes, transformed.ContentType);
            },
            cancellationToken);
    }

    public async Task MarkAlbumDownloadStartedAsync(string provider, string externalId)
    {
        try
        {
            var song = await _metadataService.GetSongAsync(provider, externalId);
            if (string.IsNullOrWhiteSpace(song?.AlbumId) || PlaylistIdHelper.IsExternalPlaylist(song.AlbumId))
            {
                return;
            }

            var (isExternalAlbum, albumProvider, type, albumExternalId) = _localLibraryService.ParseExternalId(song.AlbumId);
            if (isExternalAlbum &&
                string.Equals(type, "album", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(albumProvider) &&
                !string.IsNullOrWhiteSpace(albumExternalId))
            {
                _externalAlbumAvailabilityService.MarkDownloadStarted(albumProvider, albumExternalId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not mark album download started for {Provider}:{ExternalId}", provider, externalId);
        }
    }

    private async Task<ExternalCoverBadgeIdentity?> GetExternalCoverBadgeIdentityAsync(
        (bool isExternal, string? provider, string? type, string? externalId) parsedExternalId)
    {
        if (!parsedExternalId.isExternal ||
            string.IsNullOrWhiteSpace(parsedExternalId.provider) ||
            string.IsNullOrWhiteSpace(parsedExternalId.externalId))
        {
            return null;
        }

        if (string.Equals(parsedExternalId.type, "album", StringComparison.OrdinalIgnoreCase))
        {
            return _externalAlbumAvailabilityService.IsDownloadStarted(parsedExternalId.provider, parsedExternalId.externalId)
                ? null
                : new ExternalCoverBadgeIdentity(parsedExternalId.provider, "album", parsedExternalId.externalId);
        }

        if (string.Equals(parsedExternalId.type, "song", StringComparison.OrdinalIgnoreCase))
        {
            var albumIdentity = await GetExternalSongAlbumIdentityAsync(parsedExternalId.provider, parsedExternalId.externalId);
            if (albumIdentity != null &&
                _externalAlbumAvailabilityService.IsDownloadStarted(albumIdentity.Provider, albumIdentity.ExternalId))
            {
                return null;
            }

            return new ExternalCoverBadgeIdentity(parsedExternalId.provider, "song", parsedExternalId.externalId);
        }

        return null;
    }

    private async Task<ExternalCoverBadgeIdentity?> GetExternalSongAlbumIdentityAsync(string provider, string externalId)
    {
        try
        {
            var song = await _metadataService.GetSongAsync(provider, externalId);
            if (string.IsNullOrWhiteSpace(song?.AlbumId) || PlaylistIdHelper.IsExternalPlaylist(song.AlbumId))
            {
                return null;
            }

            var (isExternalAlbum, albumProvider, type, albumExternalId) = _localLibraryService.ParseExternalId(song.AlbumId);
            if (isExternalAlbum &&
                string.Equals(type, "album", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(albumProvider) &&
                !string.IsNullOrWhiteSpace(albumExternalId))
            {
                return new ExternalCoverBadgeIdentity(albumProvider, "album", albumExternalId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve album identity for cover badge on {Provider}:{ExternalId}", provider, externalId);
        }

        return null;
    }

    private async Task<CoverArtPayload?> ResolveAndFetchCoverArtAsync(
        string id,
        int? requestedSize,
        CancellationToken cancellationToken)
    {
        try
        {
            string? coverUrl = null;

            if (PlaylistIdHelper.IsExternalPlaylist(id))
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);
                var playlist = await _metadataService.GetPlaylistAsync(provider, externalId);
                coverUrl = playlist?.CoverUrl;
            }
            else
            {
                var (_, coverProvider, type, coverExternalId) = _localLibraryService.ParseExternalId(id);
                switch (type)
                {
                    case "artist":
                        var artist = await _metadataService.GetArtistAsync(coverProvider!, coverExternalId!);
                        coverUrl = artist?.ImageUrl;
                        break;
                    case "album":
                        coverUrl = await _metadataService.GetAlbumCoverUrlAsync(coverProvider!, coverExternalId!);
                        break;
                    case "song":
                    default:
                        var song = await _metadataService.GetSongAsync(coverProvider!, coverExternalId!);
                        coverUrl = song?.CoverArtUrlLarge ?? song?.CoverArtUrl;
                        if (coverUrl == null)
                        {
                            coverUrl = await _metadataService.GetAlbumCoverUrlAsync(coverProvider!, coverExternalId!);
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(coverUrl))
            {
                return null;
            }

            if (requestedSize.HasValue)
            {
                coverUrl = RewriteQobuzCoverSize(coverUrl, requestedSize.Value);
            }

            var sourceKey = $"cover-source:{coverUrl}";
            return await _coverArtCache.GetOrCreateAsync(
                sourceKey,
                async token =>
                {
                    var httpClient = _httpClientFactory.CreateClient(HttpClientName);
                    using var req = new HttpRequestMessage(HttpMethod.Get, coverUrl);

                    var response = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync(token);
                    var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                    return new CoverArtPayload(bytes, contentType);
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cover art for {Id}", id);
            return null;
        }
    }

    private static string RewriteQobuzCoverSize(string url, int requestedSize)
    {
        if (!url.Contains("static.qobuz.com/images/", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var target = requestedSize switch
        {
            <= 50 => "50",
            <= 150 => "150",
            <= 300 => "300",
            <= 600 => "600",
            _ => "max",
        };

        var lastUnderscore = url.LastIndexOf('_');
        var lastDot = url.LastIndexOf('.');
        if (lastUnderscore < 0 || lastDot < 0 || lastDot <= lastUnderscore)
        {
            return url;
        }

        return string.Concat(url.AsSpan(0, lastUnderscore + 1), target, url.AsSpan(lastDot));
    }

    private static string CreateCoverCacheKey(string prefix, string provider, string type, string externalId, int? requestedSize)
        => $"{prefix}:{provider}:{type}:{externalId}:{requestedSize?.ToString() ?? "original"}";

    private sealed record ExternalCoverBadgeIdentity(string Provider, string Type, string ExternalId);
}
