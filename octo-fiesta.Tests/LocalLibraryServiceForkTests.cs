using System.Net;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Domain;

namespace octo_fiesta.Tests;

public partial class LocalLibraryServiceTests
{
    [Fact]
    public async Task FindLocalSongByMetadataAsync_WhenNavidromeReturnsMatch_ReturnsLocalSongMatch()
    {
        var song = new Song { Title = "One Love", Artist = "Nas", Album = "Illmatic XX" };

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().Contains("search3")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"subsonic-response\":{\"searchResult3\":{\"song\":[" +
                    "{\"id\":\"42\",\"title\":\"One Love\",\"artist\":\"Nas\",\"album\":\"Illmatic XX\",\"path\":\"/music/Nas/Illmatic XX/07. One Love.flac\"}" +
                    "]}}}")
            });

        var service = BuildService();
        service.SetSubsonicCredentials(new Dictionary<string, string> { ["u"] = "admin", ["p"] = "pass", ["v"] = "1.16.1", ["c"] = "test" });

        var result = await service.FindLocalSongByMetadataAsync(song);

        Assert.NotNull(result);
        Assert.Equal("42", result!.LocalSubsonicId);
        Assert.Equal("/music/Nas/Illmatic XX/07. One Love.flac", result.LocalPath);
    }

    [Fact]
    public async Task FindLocalSongByMetadataAsync_WhenNoMatch_ReturnsNull()
    {
        var song = new Song { Title = "Ghost Track", Artist = "Nobody", Album = "Unknown" };

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().Contains("search3")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"subsonic-response\":{\"searchResult3\":{\"song\":[]}}}")
            });

        var service = BuildService();
        service.SetSubsonicCredentials(new Dictionary<string, string> { ["u"] = "admin", ["p"] = "pass", ["v"] = "1.16.1", ["c"] = "test" });

        var result = await service.FindLocalSongByMetadataAsync(song);

        Assert.Null(result);
    }
}
