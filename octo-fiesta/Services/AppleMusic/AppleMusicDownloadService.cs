using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.AppleMusic;

/// <summary>
/// Downloads Apple Music songs through alacarte. alacarte writes the file into
/// the shared library itself (its own layout, tags and FLAC conversion), so
/// the result points at that file instead of handing over a stream to save.
/// </summary>
public class AppleMusicDownloadService : BaseDownloadService
{
    private readonly AlacarteClient _client;
    private readonly ILogger<AppleMusicDownloadService> _logger;

    public AppleMusicDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        AlacarteClient client,
        IServiceProvider serviceProvider,
        ILogger<AppleMusicDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _client = client;
        _logger = logger;
    }

    protected override string ProviderName => AppleMusicMapper.Provider;

    public override async Task<bool> IsAvailableAsync()
    {
        if (!_client.IsConfigured)
        {
            _logger.LogWarning("Apple Music provider needs AppleMusic:AlacarteUrl and AppleMusic:ApiToken");
            return false;
        }
        return await _client.SearchAsync("a", 1, 0, 0, 0) != null;
    }

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Asking alacarte for Apple Music song {TrackId} ({Artist} - {Title})", trackId, song.Artist, song.Title);
        var result = await _client.EnsureSongAsync(trackId, cancellationToken);
        var localPath = ToLibraryPath(DownloadPath, result.Path);
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException(
                $"alacarte reported {result.Path} but it is not visible under {DownloadPath}; both must share the same library folder",
                localPath);
        }
        _logger.LogInformation("alacarte {Status} {Path}", result.Status, localPath);
        var extension = Path.GetExtension(localPath);
        return new DownloadResult(Stream.Null, extension, QualityFromExtension(extension), LibraryPath: localPath);
    }

    /// <summary>
    /// Resolves alacarte's library-relative path under our library root,
    /// refusing anything that would point outside of it.
    /// </summary>
    public static string ToLibraryPath(string libraryRoot, string relativePath)
    {
        var root = Path.GetFullPath(libraryRoot);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSlash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"alacarte returned a path outside the library: {relativePath}");
        }
        return full;
    }

    private static string? QualityFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".flac" => "FLAC",
        ".m4a" => "ALAC",
        _ => null,
    };

    protected override string? ExtractExternalIdFromAlbumId(string albumId) =>
        albumId.StartsWith(AppleMusicMapper.AlbumPrefix) ? albumId[AppleMusicMapper.AlbumPrefix.Length..] : null;

    // alacarte picks the quality; octo-fiesta must not try to upgrade its files
    protected override string? GetTargetQuality() => null;
}
