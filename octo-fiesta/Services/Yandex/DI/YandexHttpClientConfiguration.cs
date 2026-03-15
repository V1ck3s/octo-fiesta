using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Yandex;

public static class YandexHttpClientConfiguration
{
    public static void ConfigureClient(IServiceProvider sp, HttpClient client)
    {
        var settings = sp.GetRequiredService<IOptions<YandexSettings>>().Value;
        client.BaseAddress = new Uri("https://api.music.yandex.net");

        client.DefaultRequestHeaders.Add("X-Yandex-Music-Client", "YandexMusicDesktopAppWindows/5.13.2");
        client.DefaultRequestHeaders.Add("Accept-Language", settings.Language);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", settings.OAuthToken);
    }
}