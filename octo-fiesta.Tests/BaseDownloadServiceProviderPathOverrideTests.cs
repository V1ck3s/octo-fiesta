using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using octo_fiesta.Services;

namespace octo_fiesta.Tests;

/// <summary>
/// Covers the "{ProviderName}:DownloadPath" override (e.g. Deezer__DownloadPath) that lets a
/// single provider resolve downloads under its own folder instead of the shared
/// "Library:DownloadPath" - e.g. when that provider
/// must write into a library folder octo-fiesta doesn't otherwise write into. No provider opt-in is
/// required: the lookup reuses each provider's existing (lowercase) ProviderName directly,
/// relying on IConfiguration's case-insensitive keys to match the PascalCase env var section.
/// </summary>
public class BaseDownloadServiceProviderPathOverrideTests : IDisposable
{
    private readonly string _sharedPath;
    private readonly string _providerPath;

    public BaseDownloadServiceProviderPathOverrideTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "octo-fiesta-provider-path-tests-" + Guid.NewGuid());
        _sharedPath = Path.Combine(root, "shared");
        _providerPath = Path.Combine(root, "music");
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_sharedPath)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private TService BuildService<TService>(
        Func<IHttpClientFactory, IConfiguration, ILocalLibraryService, IMusicMetadataService, SubsonicSettings, IServiceProvider, Microsoft.Extensions.Logging.ILogger, TService> factory,
        Dictionary<string, string?> configValues)
        where TService : BaseDownloadService
    {
        var settings = new SubsonicSettings
        {
            FolderTemplate = "{artist}/{album}/{track}. {title}",
            StorageMode = StorageMode.Permanent
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
        return factory(
            new Mock<IHttpClientFactory>().Object,
            config,
            new Mock<ILocalLibraryService>().Object,
            new Mock<IMusicMetadataService>().Object,
            settings,
            new Mock<IServiceProvider>().Object,
            NullLogger.Instance);
    }

    [Fact]
    public void NoMatchingProviderSection_UsesSharedLibraryDownloadPath()
    {
        var service = BuildService(
            (h, c, l, m, s, sp, lg) => new PlainProviderDownloadService(h, c, l, m, s, sp, lg),
            new Dictionary<string, string?> { ["Library:DownloadPath"] = _sharedPath });

        Assert.Equal(_sharedPath, service.EffectiveDownloadPath);
    }

    [Fact]
    public void ProviderOverrideSet_TakesPrecedenceOverSharedLibraryDownloadPath()
    {
        var service = BuildService(
            (h, c, l, m, s, sp, lg) => new ProviderLikeDownloadService(h, c, l, m, s, sp, lg),
            new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _sharedPath,
                // PascalCase, as an Deezer__DownloadPath env var would bind - ProviderName
                // itself is lowercase ("deezer"), proving the lookup is case-insensitive.
                ["Deezer:DownloadPath"] = _providerPath,
            });

        Assert.Equal(_providerPath, service.EffectiveDownloadPath);
    }

    [Fact]
    public void ProviderOverrideConfiguredForADifferentProvider_IsIgnored()
    {
        // Deezer's own override must not leak into a provider that didn't ask for one.
        var service = BuildService(
            (h, c, l, m, s, sp, lg) => new PlainProviderDownloadService(h, c, l, m, s, sp, lg),
            new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _sharedPath,
                ["Deezer:DownloadPath"] = _providerPath,
            });

        Assert.Equal(_sharedPath, service.EffectiveDownloadPath);
    }

    private abstract class FakeDownloadServiceBase : BaseDownloadService
    {
        public string EffectiveDownloadPath => DownloadPath;

        protected FakeDownloadServiceBase(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILocalLibraryService localLibraryService,
            IMusicMetadataService metadataService,
            SubsonicSettings subsonicSettings,
            IServiceProvider serviceProvider,
            Microsoft.Extensions.Logging.ILogger logger)
            : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings, serviceProvider, logger)
        {
        }

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => null;

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
            => throw new NotImplementedException("Not exercised by these tests.");
    }

    // A provider name with no matching config section anywhere (mirrors Deezer/Qobuz/etc. when
    // the user hasn't set a per-provider override for them).
    private sealed class PlainProviderDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        SubsonicSettings subsonicSettings,
        IServiceProvider serviceProvider,
        Microsoft.Extensions.Logging.ILogger logger)
        : FakeDownloadServiceBase(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings, serviceProvider, logger)
    {
        protected override string ProviderName => "fake";
    }

    // Mirrors a real provider (Deezer).
    private sealed class ProviderLikeDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        SubsonicSettings subsonicSettings,
        IServiceProvider serviceProvider,
        Microsoft.Extensions.Logging.ILogger logger)
        : FakeDownloadServiceBase(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings, serviceProvider, logger)
    {
        protected override string ProviderName => "deezer";
    }
}
