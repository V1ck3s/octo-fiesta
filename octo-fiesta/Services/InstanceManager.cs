using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services;

/// <summary>
/// Represents the type of instance group.
/// </summary>
public enum InstanceType
{
    /// <summary>API instances for metadata requests.</summary>
    Api,
    /// <summary>Streaming instances for audio/track requests.</summary>
    Streaming
}

/// <summary>
/// Represents an instance with its base URL and speed test results.
/// </summary>
public class InstanceInfo
{
    /// <summary>Base URL of the instance.</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Latency in milliseconds from the last speed test. Double.MaxValue indicates failure.</summary>
    [JsonPropertyName("latencyMs")]
    public double LatencyMs { get; set; } = double.MaxValue;

    /// <summary>Last error message if speed test failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Timestamp of the last speed test.</summary>
    [JsonPropertyName("lastTested")]
    public DateTimeOffset? LastTested { get; set; }
}

/// <summary>
/// Grouped instances for api and streaming.
/// </summary>
public class InstanceGroups
{
    /// <summary>API instances.</summary>
    [JsonPropertyName("api")]
    public List<string> Api { get; set; } = new();

    /// <summary>Streaming instances.</summary>
    [JsonPropertyName("streaming")]
    public List<string> Streaming { get; set; } = new();
}

/// <summary>
/// Manages loading, speed testing, caching, and persistence of provider instances.
/// Thread-safe and cancellation-token friendly.
/// </summary>
public class InstanceManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly InstanceOptions _options;
    private readonly ILogger<InstanceManager> _logger;

    private readonly ConcurrentDictionary<InstanceType, List<InstanceInfo>> _instances = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _speedTestLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    // Test endpoint paths per type.
    // These call lightweight, stable catalog resources used only for health checks and latency measurements.
    // If these IDs become invalid or unsuitable, update them to new lightweight, stable resources as described in
    // the instance configuration/operations documentation.
    private const string ApiTestPath = "artist/?id=3532302";
    private const string StreamingTestPath = "track/?id=204567804&quality=HIGH";

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceManager"/> class.
    /// </summary>
    public InstanceManager(
        IHttpClientFactory httpClientFactory,
        IOptions<InstanceOptions> options,
        ILogger<InstanceManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Loads instances from the configured URL or falls back to defaults.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Grouped instances.</returns>
    public async Task<InstanceGroups> LoadInstancesFromGitHubAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(_options.InstancesUrl))
            {
                _logger.LogDebug("InstancesUrl not configured; using defaults");
                return GetDefaultInstances();
            }

            var client = _httpClientFactory.CreateClient("instanceLoader");
            client.Timeout = TimeSpan.FromSeconds(10);

            try
            {
                var response = await client.GetAsync(_options.InstancesUrl, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var groups = ParseInstancesJson(json);

                _logger.LogInformation("Loaded {ApiCount} API and {StreamingCount} streaming instances from remote",
                    groups.Api.Count, groups.Streaming.Count);

                return groups;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load instances from remote, using defaults");
                return GetDefaultInstances();
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Parses JSON that can be either an array (legacy) or an object with api/streaming groups.
    /// </summary>
    public InstanceGroups ParseInstancesJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            // Legacy format: array applies to both groups
            var instanceUrls = new List<string>();
            foreach (var element in root.EnumerateArray())
            {
                var url = element.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    instanceUrls.Add(url);
            }

            return new InstanceGroups
            {
                Api = new List<string>(instanceUrls),
                Streaming = new List<string>(instanceUrls)
            };
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var groups = new InstanceGroups();

            if (root.TryGetProperty("api", out var apiElement) && apiElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in apiElement.EnumerateArray())
                {
                    var url = element.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                        groups.Api.Add(url);
                }
            }

            if (root.TryGetProperty("streaming", out var streamingElement) && streamingElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in streamingElement.EnumerateArray())
                {
                    var url = element.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                        groups.Streaming.Add(url);
                }
            }

            return groups;
        }

        return GetDefaultInstances();
    }

    /// <summary>
    /// Gets the default instances from configuration.
    /// </summary>
    public InstanceGroups GetDefaultInstances()
    {
        return new InstanceGroups
        {
            Api = new List<string>(_options.DefaultApiInstances),
            Streaming = new List<string>(_options.DefaultStreamingInstances)
        };
    }

    /// <summary>
    /// Speed tests a single instance.
    /// </summary>
    /// <param name="baseUrl">Base URL of the instance.</param>
    /// <param name="type">Type of instance (api or streaming).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>InstanceInfo with test results.</returns>
    public async Task<InstanceInfo> SpeedTestInstanceAsync(string baseUrl, InstanceType type, CancellationToken cancellationToken = default)
    {
        var info = new InstanceInfo
        {
            BaseUrl = baseUrl,
            LastTested = DateTimeOffset.UtcNow
        };

        var testPath = type == InstanceType.Api ? ApiTestPath : StreamingTestPath;
        var testUrl = CombineUrl(baseUrl, testPath);

        var client = _httpClientFactory.CreateClient("instanceTester");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.SpeedTestTimeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                info.LatencyMs = stopwatch.Elapsed.TotalMilliseconds;
                info.Error = null;
            }
            else
            {
                info.LatencyMs = double.MaxValue;
                info.Error = $"HTTP {(int)response.StatusCode}";
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            info.LatencyMs = double.MaxValue;
            info.Error = "Timeout";
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            info.LatencyMs = double.MaxValue;
            info.Error = ex.Message;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            info.LatencyMs = double.MaxValue;
            info.Error = ex.Message;
        }

        return info;
    }

    /// <summary>
    /// Gets instances sorted by latency, running speed tests for any missing results.
    /// </summary>
    /// <param name="type">Type of instances to get.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of instances sorted by latency (ascending).</returns>
    public async Task<List<InstanceInfo>> GetInstancesAsync(InstanceType type, CancellationToken cancellationToken = default)
    {
        // Try to load from cache first
        if (!_instances.TryGetValue(type, out var cachedInstances) || cachedInstances.Count == 0)
        {
            // Try to load from persisted file
            cachedInstances = await LoadPersistedInstancesAsync(type, cancellationToken);

            if (cachedInstances == null || cachedInstances.Count == 0)
            {
                // Load from remote/defaults and run speed tests
                var groups = await LoadInstancesFromGitHubAsync(cancellationToken);
                var urls = type == InstanceType.Api ? groups.Api : groups.Streaming;

                cachedInstances = new List<InstanceInfo>();
                foreach (var url in urls)
                {
                    var info = await SpeedTestInstanceAsync(url, type, cancellationToken);
                    cachedInstances.Add(info);
                }
            }

            // Sort by latency
            cachedInstances = SortInstancesByLatency(cachedInstances);
            _instances[type] = cachedInstances;

            // Persist
            await PersistInstancesAsync(type, cachedInstances, cancellationToken);
        }

        return cachedInstances.ToList();
    }

    /// <summary>
    /// Refreshes speed tests for all instances of a given type.
    /// </summary>
    public async Task RefreshSpeedTestsAsync(InstanceType type, CancellationToken cancellationToken = default)
    {
        await _speedTestLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Refreshing speed tests for {Type} instances", type);

            var groups = await LoadInstancesFromGitHubAsync(cancellationToken);
            var urls = type == InstanceType.Api ? groups.Api : groups.Streaming;

            var newInstances = new List<InstanceInfo>();
            foreach (var url in urls)
            {
                var info = await SpeedTestInstanceAsync(url, type, cancellationToken);
                newInstances.Add(info);
            }

            // Sort by latency
            var sorted = SortInstancesByLatency(newInstances);
            _instances[type] = sorted;

            // Persist
            await PersistInstancesAsync(type, sorted, cancellationToken);

            _logger.LogInformation("Refreshed {Count} {Type} instances", sorted.Count, type);
        }
        finally
        {
            _speedTestLock.Release();
        }
    }

    /// <summary>
    /// Refreshes all instance types.
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await RefreshSpeedTestsAsync(InstanceType.Api, cancellationToken);
        await RefreshSpeedTestsAsync(InstanceType.Streaming, cancellationToken);
    }

    /// <summary>
    /// Reorders instances by moving one from a source index to a destination index.
    /// </summary>
    public async Task ReorderInstancesAsync(InstanceType type, int fromIndex, int toIndex, CancellationToken cancellationToken = default)
    {
        await _speedTestLock.WaitAsync(cancellationToken);
        try
        {
            if (!_instances.TryGetValue(type, out var instances))
            {
                instances = await GetInstancesAsync(type, cancellationToken);
            }

            var count = instances.Count;
            if (count == 0)
            {
                throw new InvalidOperationException("No instances available to reorder");
            }

            if (fromIndex < 0 || fromIndex >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(fromIndex), $"fromIndex must be in [0, {count - 1}]. Actual value: {fromIndex}");
            }

            if (toIndex < 0 || toIndex >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(toIndex), $"toIndex must be in [0, {count - 1}]. Actual value: {toIndex}");
            }
            // No-op if positions are the same
            if (fromIndex == toIndex)
                return;

            var item = instances[fromIndex];
            instances.RemoveAt(fromIndex);

            // Removing at fromIndex shifts subsequent elements left, so when moving forward
            // (toIndex > fromIndex) the effective insert index becomes toIndex - 1.
            var insertIndex = toIndex > fromIndex ? toIndex - 1 : toIndex; // Adjust for prior removal when moving forward
            instances.Insert(insertIndex, item);

            // Update cache and persist
            _instances[type] = instances;
            await PersistInstancesAsync(type, instances, cancellationToken);
        }
        finally
        {
            _speedTestLock.Release();
        }
    }

    /// <summary>
    /// Gets the storage file path for a given instance type.
    /// </summary>
    private string GetStorageFilePath(InstanceType type)
    {
        var filename = type == InstanceType.Api ? "instances-api.json" : "instances-streaming.json";
        return Path.Combine(_options.StoragePath, filename);
    }

    /// <summary>
    /// Persists instances to disk.
    /// </summary>
    private async Task PersistInstancesAsync(InstanceType type, List<InstanceInfo> instances, CancellationToken cancellationToken)
    {
        var filePath = GetStorageFilePath(type);
        try
        {
            var directory = _options.StoragePath;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(instances, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            _logger.LogDebug("Persisted {Count} {Type} instances to {Path}", instances.Count, type, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist {Type} instances to {Path}", type, filePath);
        }
    }

    /// <summary>
    /// Loads persisted instances from disk.
    /// </summary>
    private async Task<List<InstanceInfo>?> LoadPersistedInstancesAsync(InstanceType type, CancellationToken cancellationToken)
    {
        try
        {
            var filePath = GetStorageFilePath(type);
            if (!File.Exists(filePath))
                return null;

            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var instances = JsonSerializer.Deserialize<List<InstanceInfo>>(json, JsonOptions);

            _logger.LogDebug("Loaded {Count} {Type} instances from {Path}",
                instances?.Count ?? 0, type, filePath);

            return instances;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted {Type} instances", type);
            return null;
        }
    }

    /// <summary>
    /// Sorts instances by latency (ascending).
    /// </summary>
    private static List<InstanceInfo> SortInstancesByLatency(List<InstanceInfo> instances)
    {
        return instances.OrderBy(i => i.LatencyMs).ToList();
    }

    /// <summary>
    /// Combines a base URL with a relative path.
    /// </summary>
    public static string CombineUrl(string baseUrl, string relativePath)
    {
        return baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/');
    }
}
