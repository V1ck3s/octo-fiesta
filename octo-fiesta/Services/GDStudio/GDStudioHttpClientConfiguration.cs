using System.Net;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.GDStudio;

public static class GDStudioHttpClientConfiguration
{
    public const string ClientName = "GDStudio";

    public static HttpMessageHandler CreateHandler(IServiceProvider sp)
    {
        var proxy = sp.GetRequiredService<IOptions<GDStudioSettings>>().Value.Proxy;
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(new Uri(proxy.Trim()));
            handler.UseProxy = true;
        }
        return handler;
    }
}
