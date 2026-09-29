using System.Net;
using Microsoft.Extensions.DependencyInjection;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Services;

public static class ForkFeatures
{
    public static IServiceCollection AddForkFeatures(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMemoryCache(options => options.SizeLimit = 512);

        services.AddHttpClient(ExternalCoverArtService.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                MaxConnectionsPerServer = 32,
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.Configure<ExternalCoverSettings>(configuration.GetSection("ExternalCover"));
        services.AddSingleton<ICoverArtTransformer, CoverArtTransformer>();
        services.AddSingleton<ICoverArtCache, CoverArtCache>();
        services.AddSingleton<IExternalAlbumAvailabilityService, ExternalAlbumAvailabilityService>();
        services.AddSingleton<IExternalCoverArtService, ExternalCoverArtService>();
        services.AddSingleton<INavidromeUploadService, NavidromeUploadService>();

        return services;
    }
}
