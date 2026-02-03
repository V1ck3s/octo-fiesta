using octo_fiesta.Services;
using octo_fiesta.Models.Settings;
using Moq;
using Moq.Protected;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace octo_fiesta.Tests;

public class InstanceManagerTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<ILogger<InstanceManager>> _loggerMock;
    private readonly InstanceOptions _options;

    public InstanceManagerTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);

        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        _loggerMock = new Mock<ILogger<InstanceManager>>();

        _options = new InstanceOptions
        {
            InstancesUrl = "https://raw.githubusercontent.com/SamidyFR/monochrome/6d19792cd949e3389ee1c787e9378e12d4b252e6/public/instances.json",
            StoragePath = Path.Combine(Path.GetTempPath(), "instance-tests-" + Guid.NewGuid()),
            SpeedTestTimeoutMs = 10000,
            DefaultApiInstances = new[] { "https://api.monochrome.tf" },
            DefaultStreamingInstances = new[] { "https://api.monochrome.tf" }
        };
    }

    private InstanceManager CreateManager()
    {
        return new InstanceManager(
            _httpClientFactoryMock.Object,
            Options.Create(_options),
            _loggerMock.Object);
    }

    private void SetupHttpResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
    }

    #region LoadInstancesFromGitHubAsync Tests

    [Fact]
    public async Task LoadInstancesFromGitHubAsync_WithArrayFormat_ReturnsBothGroups()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new[]
        {
            "https://api1.example.com",
            "https://api2.example.com"
        });
        SetupHttpResponse(json);
        var manager = CreateManager();

        // Act
        var result = await manager.LoadInstancesFromGitHubAsync();

        // Assert
        Assert.Equal(2, result.Api.Count);
        Assert.Equal(2, result.Streaming.Count);
        Assert.Contains("https://api1.example.com", result.Api);
        Assert.Contains("https://api2.example.com", result.Api);
    }

    [Fact]
    public async Task LoadInstancesFromGitHubAsync_WithObjectFormat_ReturnsGroupedLists()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new
        {
            api = new[] { "https://api1.example.com", "https://api2.example.com" },
            streaming = new[] { "https://stream1.example.com" }
        });
        SetupHttpResponse(json);
        var manager = CreateManager();

        // Act
        var result = await manager.LoadInstancesFromGitHubAsync();

        // Assert
        Assert.Equal(2, result.Api.Count);
        Assert.Single(result.Streaming);
        Assert.Contains("https://api1.example.com", result.Api);
        Assert.Contains("https://stream1.example.com", result.Streaming);
    }

    [Fact]
    public async Task LoadInstancesFromGitHubAsync_OnHttpFailure_UsesFallbackDefaults()
    {
        // Arrange
        SetupHttpResponse("", HttpStatusCode.InternalServerError);
        var manager = CreateManager();

        // Act
        var result = await manager.LoadInstancesFromGitHubAsync();

        // Assert
        Assert.Equal(_options.DefaultApiInstances, result.Api);
        Assert.Equal(_options.DefaultStreamingInstances, result.Streaming);
    }

    [Fact]
    public async Task LoadInstancesFromGitHubAsync_OnNetworkError_UsesFallbackDefaults()
    {
        // Arrange
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var manager = CreateManager();

        // Act
        var result = await manager.LoadInstancesFromGitHubAsync();

        // Assert
        Assert.Equal(_options.DefaultApiInstances, result.Api);
        Assert.Equal(_options.DefaultStreamingInstances, result.Streaming);
    }

    #endregion

    #region ParseInstancesJson Tests

    [Fact]
    public void ParseInstancesJson_WithLegacyArray_ParsesCorrectly()
    {
        // Arrange
        var json = "[\"https://a.com\", \"https://b.com\"]";
        var manager = CreateManager();

        // Act
        var result = manager.ParseInstancesJson(json);

        // Assert
        Assert.Equal(2, result.Api.Count);
        Assert.Equal(2, result.Streaming.Count);
    }

    [Fact]
    public void ParseInstancesJson_WithGroupedObject_ParsesCorrectly()
    {
        // Arrange
        var json = "{\"api\":[\"https://a.com\"],\"streaming\":[\"https://b.com\",\"https://c.com\"]}";
        var manager = CreateManager();

        // Act
        var result = manager.ParseInstancesJson(json);

        // Assert
        Assert.Single(result.Api);
        Assert.Equal(2, result.Streaming.Count);
    }

    #endregion

    #region SpeedTestInstanceAsync Tests

    [Fact]
    public async Task SpeedTestInstanceAsync_OnSuccess_ReturnsLatency()
    {
        // Arrange
        SetupHttpResponse("OK");
        var manager = CreateManager();

        // Act
        var result = await manager.SpeedTestInstanceAsync("https://test.example.com", InstanceType.Api);

        // Assert
        Assert.Equal("https://test.example.com", result.BaseUrl);
        Assert.True(result.LatencyMs < double.MaxValue);
        Assert.Null(result.Error);
        Assert.NotNull(result.LastTested);
    }

    [Fact]
    public async Task SpeedTestInstanceAsync_OnHttpError_ReturnsInfinityLatency()
    {
        // Arrange
        SetupHttpResponse("", HttpStatusCode.InternalServerError);
        var manager = CreateManager();

        // Act
        var result = await manager.SpeedTestInstanceAsync("https://test.example.com", InstanceType.Api);

        // Assert
        Assert.Equal(double.MaxValue, result.LatencyMs);
        Assert.Equal("HTTP 500", result.Error);
    }

    [Fact]
    public async Task SpeedTestInstanceAsync_OnNetworkError_ReturnsInfinityLatency()
    {
        // Arrange
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var manager = CreateManager();

        // Act
        var result = await manager.SpeedTestInstanceAsync("https://test.example.com", InstanceType.Api);

        // Assert
        Assert.Equal(double.MaxValue, result.LatencyMs);
        Assert.Contains("Connection refused", result.Error);
    }

    [Fact]
    public async Task SpeedTestInstanceAsync_OnTimeout_ReturnsInfinityLatency()
    {
        // Arrange
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var manager = CreateManager();

        // Act
        var result = await manager.SpeedTestInstanceAsync("https://test.example.com", InstanceType.Api);

        // Assert
        Assert.Equal(double.MaxValue, result.LatencyMs);
        Assert.Equal("Timeout", result.Error);
    }

    #endregion

    #region GetInstancesAsync Tests

    [Fact]
    public async Task GetInstancesAsync_SortsByLatency()
    {
        // Arrange
        var callCount = 0;
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                callCount++;
                // First call: load instances (success)
                // Second call: speed test slow instance (simulated delay)
                // Third call: speed test fast instance (immediate)
                if (callCount == 1)
                {
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent("[\"https://slow.com\", \"https://fast.com\"]")
                    });
                }

                if (callCount == 2)
                {
                    // simulate slower response for the first speed test (synchronous sleep is acceptable in test)
                    System.Threading.Thread.Sleep(100);
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent("OK")
                    });
                }

                // fast instance - immediate response
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("OK")
                });
            });

        var manager = CreateManager();

        // Act
        var result = await manager.GetInstancesAsync(InstanceType.Api);

        // Assert
        Assert.Equal(2, result.Count);
        // Both should have latency (success responses)
        Assert.All(result, i => Assert.True(i.LatencyMs < double.MaxValue));
        // Fast instance should be first after sorting by latency
        Assert.Equal("https://fast.com", result[0].BaseUrl);
    }

    [Fact]
    public async Task GetInstancesAsync_PersistsAndRestoresOrdering()
    {
        // Arrange
        var json = "[\"https://api1.example.com\"]";
        SetupHttpResponse(json);
        var manager = CreateManager();

        try
        {
            // Act - First call loads and persists
            var result1 = await manager.GetInstancesAsync(InstanceType.Api);

            // Create new manager to test loading from disk
            var manager2 = CreateManager();
            var result2 = await manager2.GetInstancesAsync(InstanceType.Api);

            // Assert
            Assert.Single(result1);
            Assert.Single(result2);
            Assert.Equal(result1[0].BaseUrl, result2[0].BaseUrl);
        }
        finally
        {
            if (Directory.Exists(_options.StoragePath))
            {
                Directory.Delete(_options.StoragePath, true);
            }
        }
    }

    #endregion

    #region CombineUrl Tests

    [Fact]
    public void CombineUrl_HandlesTrailingSlashOnBase()
    {
        var result = InstanceManager.CombineUrl("https://example.com/", "path/to/resource");
        Assert.Equal("https://example.com/path/to/resource", result);
    }

    [Fact]
    public void CombineUrl_HandlesNoTrailingSlashOnBase()
    {
        var result = InstanceManager.CombineUrl("https://example.com", "path/to/resource");
        Assert.Equal("https://example.com/path/to/resource", result);
    }

    [Fact]
    public void CombineUrl_HandlesLeadingSlashOnPath()
    {
        var result = InstanceManager.CombineUrl("https://example.com", "/path/to/resource");
        Assert.Equal("https://example.com/path/to/resource", result);
    }

    [Fact]
    public void CombineUrl_HandlesBothSlashes()
    {
        var result = InstanceManager.CombineUrl("https://example.com/", "/path/to/resource");
        Assert.Equal("https://example.com/path/to/resource", result);
    }

    #endregion
}
