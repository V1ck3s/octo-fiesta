using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Services.AppleMusic;

public static class AppleMusicRegistration
{
    /// <summary>
    /// Apple Music is enabled by pointing octo-fiesta at an alacarte instance.
    /// </summary>
    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["AppleMusic:AlacarteUrl"]) &&
        !string.IsNullOrWhiteSpace(configuration["AppleMusic:ApiToken"]);

    public static void AddClient(IServiceCollection services)
    {
        services.AddHttpClient(AlacarteClient.HttpClientName, AlacarteClient.ConfigureClient);
        services.AddSingleton<AlacarteClient>();
    }

    /// <summary>
    /// Apple Music as the only provider (Subsonic:MusicService=AppleMusic).
    /// </summary>
    public static void AddAppleMusicAsPrimary(IServiceCollection services, bool enableExternalPlaylists)
    {
        AddClient(services);
        if (enableExternalPlaylists)
        {
            services.AddSingleton<PlaylistSyncService>();
        }
        services.AddSingleton<IMusicMetadataService, AppleMusicMetadataService>();
        services.AddSingleton<IDownloadService, AppleMusicDownloadService>();
    }

    /// <summary>
    /// Adds Apple Music next to the primary provider registered before this
    /// call. The primary keeps its own registration (so PlaylistSyncService and
    /// friends still find it), and the multi-provider services are registered
    /// last so they are what single IMusicMetadataService/IDownloadService
    /// consumers receive.
    /// </summary>
    public static void AddAppleMusicAlongside(IServiceCollection services, bool enableExternalPlaylists)
    {
        AddClient(services);
        var primaryMetadata = KeepAsConcrete<IMusicMetadataService>(services);
        var primaryDownload = KeepAsConcrete<IDownloadService>(services);

        if (enableExternalPlaylists && services.All(d => d.ServiceType != typeof(PlaylistSyncService)))
        {
            services.AddSingleton<PlaylistSyncService>();
        }

        services.AddSingleton<AppleMusicMetadataService>();
        services.AddSingleton<AppleMusicDownloadService>();
        services.AddSingleton<IMusicMetadataService>(sp => sp.GetRequiredService<AppleMusicMetadataService>());
        services.AddSingleton<IDownloadService>(sp => sp.GetRequiredService<AppleMusicDownloadService>());

        services.AddSingleton<IMusicMetadataService>(sp => new MultiProviderMetadataService(
            (IMusicMetadataService)sp.GetRequiredService(primaryMetadata),
            sp.GetRequiredService<AppleMusicMetadataService>()));
        services.AddSingleton<IDownloadService>(sp => new MultiProviderDownloadService(
            (IDownloadService)sp.GetRequiredService(primaryDownload),
            sp.GetRequiredService<AppleMusicDownloadService>()));
    }

    // Re-registers the last TService implementation as its own type, still
    // exposed as TService, so it can be resolved directly without a cycle.
    private static Type KeepAsConcrete<TService>(IServiceCollection services)
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(TService));
        var implementation = descriptor.ImplementationType
            ?? throw new InvalidOperationException($"{typeof(TService).Name} must be registered by type to add Apple Music");
        services.Remove(descriptor);
        services.AddSingleton(implementation);
        services.AddSingleton(typeof(TService), sp => sp.GetRequiredService(implementation));
        return implementation;
    }
}
