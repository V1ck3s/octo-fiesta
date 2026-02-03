using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services;

/// <summary>
/// HTTP client that uses instance rotation and retry logic when making requests.
/// </summary>
public class ApiClient
{
    private readonly InstanceManager _instanceManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApiClient> _logger;

    // Global index for round-robin rotation across requests
    private int _globalApiIndex;
    private int _globalStreamingIndex;

    // Retry delay constants (in milliseconds)
    private const int RetryDelayAfterRateLimitMs = 500;
    private const int RetryDelayAfterErrorMs = 200;

    // Simple circuit-breaker / health tracking per instance
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int FailCount, DateTime? BlackoutUntil)> _instanceHealth = new();
    private const int MaxFailuresBeforeBlackout = 3;
    private const int BaseBlackoutSeconds = 30;
    private const int MaxBlackoutSeconds = 300;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClient"/> class.
    /// </summary>
    public ApiClient(
        InstanceManager instanceManager,
        IHttpClientFactory httpClientFactory,
        ILogger<ApiClient> logger)
    {
        _instanceManager = instanceManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches a resource with automatic instance rotation and retry logic.
    /// </summary>
    /// <param name="relativePath">The relative path to request.</param>
    /// <param name="type">The type of instance to use (api or streaming).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HttpResponseMessage on success (caller responsible for disposal).</returns>
    /// <exception cref="HttpRequestException">Thrown when all attempts fail.</exception>
    public async Task<HttpResponseMessage> FetchWithRetryAsync(
        string relativePath,
        InstanceType type,
        CancellationToken cancellationToken = default)
    {
        var instances = await _instanceManager.GetInstancesAsync(type, cancellationToken);
        if (instances.Count == 0)
        {
            throw new InvalidOperationException("No instances available");
        }

        // Prefer instances that are not currently blacked out
        var now = DateTime.UtcNow;
        var candidates = instances.Where(i =>
        {
            if (_instanceHealth.TryGetValue(i.BaseUrl, out var meta) && meta.BlackoutUntil.HasValue)
            {
                return meta.BlackoutUntil.Value <= now;
            }
            return true;
        }).ToList();

        if (candidates.Count == 0)
        {
            // If all are blacked out, fall back to full list but log a warning
            _logger.LogWarning("All instances temporarily deprioritized/blackout; will attempt them anyway");
            candidates = instances.ToList();
        }

        var maxTotalAttempts = candidates.Count * 3; // allow more attempts to recover
        var startIndex = GetAndIncrementIndex(type) % candidates.Count;

        Exception? lastException = null;
        var client = _httpClientFactory.CreateClient("apiClient");

        for (var attempt = 0; attempt < maxTotalAttempts; attempt++)
        {
            var instanceIndex = (startIndex + attempt) % candidates.Count;
            var instance = candidates[instanceIndex];
            var url = InstanceManager.CombineUrl(instance.BaseUrl, relativePath);

            try
            {
                _logger.LogDebug("Attempt {Attempt}/{Max}: {Url} (failCount={FailCount})",
                    attempt + 1, maxTotalAttempts, url, _instanceHealth.TryGetValue(instance.BaseUrl, out var m) ? m.FailCount : 0);

                var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    // Success: reset health
                    _instanceHealth.TryRemove(instance.BaseUrl, out _);
                    return response;
                }

                var statusCode = (int)response.StatusCode;

                // Handle 429 - Rate limited (honor Retry-After if provided)
                if (statusCode == 429)
                {
                    _logger.LogWarning("Rate limited by {Instance}, rotating to next", instance.BaseUrl);

                    var delay = GetRetryAfterDelay(response) ?? TimeSpan.FromMilliseconds(RetryDelayAfterRateLimitMs);
                    response.Dispose();

                    // Ensure non-negative wait and bound to a sensible maximum (30s)
                    var boundedDelay = TimeSpan.FromMilliseconds(Math.Min(Math.Max(0, (int)delay.TotalMilliseconds), 30000));
                    await Task.Delay(boundedDelay, cancellationToken);

                    RecordInstanceFailure(instance.BaseUrl);
                    continue;
                }

                // Handle 401 - Check for subStatus 11002
                if (statusCode == 401)
                {
                    if (await IsInstanceAuthFailureAsync(response, cancellationToken))
                    {
                        _logger.LogWarning("Instance auth failure at {Instance}, skipping", instance.BaseUrl);
                        response.Dispose();
                        RecordInstanceFailure(instance.BaseUrl);
                        continue;
                    }
                }

                // Handle 5xx - Server errors
                if (statusCode >= 500)
                {
                    _logger.LogWarning("Server error {StatusCode} from {Instance}, skipping", statusCode, instance.BaseUrl);
                    response.Dispose();
                    RecordInstanceFailure(instance.BaseUrl);
                    await Task.Delay(RetryDelayAfterErrorMs, cancellationToken);
                    continue;
                }

                // Other 4xx responses are returned to the caller
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout
                _logger.LogWarning("Timeout for {Instance}, skipping", instance.BaseUrl);
                lastException = new HttpRequestException($"Request to {instance.BaseUrl} timed out");
                RecordInstanceFailure(instance.BaseUrl);
                await Task.Delay(RetryDelayAfterErrorMs, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Network error for {Instance}, skipping", instance.BaseUrl);
                lastException = ex;
                RecordInstanceFailure(instance.BaseUrl);
                await Task.Delay(RetryDelayAfterErrorMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // User cancellation
                throw;
            }

            // Exponential backoff between rounds (bounded): only delay after all instances have been tried
            var isEndOfRound = (attempt + 1) % candidates.Count == 0;
            var hasMoreAttempts = attempt < maxTotalAttempts - 1;
            if (isEndOfRound && hasMoreAttempts)
            {
                // Round number starts from 0 and increases each time we've tried all candidates
                var roundNumber = (attempt + 1) / candidates.Count;
                const int BaseBackoffDelayMs = 200;
                const int MaxBackoffExponent = 6;
                const int MaxBackoffDelayMs = 20000;
                var clampedRound = Math.Min(roundNumber, MaxBackoffExponent);
                var exponentialDelay = BaseBackoffDelayMs * (1 << clampedRound);
                var delayMs = Math.Min(exponentialDelay, MaxBackoffDelayMs);
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        _logger.LogError("All {Attempts} attempts failed", maxTotalAttempts);
        throw lastException ?? new HttpRequestException("All request attempts failed");
    }

    private void RecordInstanceFailure(string baseUrl)
    {
        _instanceHealth.AddOrUpdate(baseUrl,
            (1, DateTime.UtcNow.AddSeconds(BaseBlackoutSeconds)),
            (k, v) =>
            {
                var newCount = v.FailCount + 1;
                if (newCount >= MaxFailuresBeforeBlackout)
                {
                    var multiplier = CalculateBlackoutMultiplier(newCount);
                    var blackout = DateTime.UtcNow.AddSeconds(BaseBlackoutSeconds * multiplier);
                    _logger.LogWarning("Instance {Instance} entered blackout for {Seconds}s after {Failures} failures", baseUrl, (BaseBlackoutSeconds * multiplier), newCount);
                    return (newCount, blackout);
                }
                return (newCount, v.BlackoutUntil);
            });
    }

    /// <summary>
    /// Calculates an exponential blackout multiplier from the number of consecutive failures.
    /// </summary>
    /// <param name="failCount">
    /// Total number of consecutive failures recorded for the instance.
    /// For values lower than <see cref="MaxFailuresBeforeBlackout"/>, the multiplier is <c>1</c>,
    /// meaning the base blackout duration (<see cref="BaseBlackoutSeconds"/>) is used.
    /// Once <paramref name="failCount"/> is greater than or equal to <see cref="MaxFailuresBeforeBlackout"/>,
    /// the blackout duration grows exponentially with each additional failure.
    /// </param>
    /// <returns>
    /// An integer multiplier to be applied to <see cref="BaseBlackoutSeconds"/> when computing the blackout duration.
    /// From <see cref="MaxFailuresBeforeBlackout"/> onwards, the ideal multiplier is
    /// <c>2^(failCount - MaxFailuresBeforeBlackout)</c>, but it is first capped by a safety exponent
    /// limit and then clamped so that <c>multiplier * BaseBlackoutSeconds</c> does not exceed
    /// <see cref="MaxBlackoutSeconds"/>. The final value is always at least <c>1</c>.
    /// </returns>
    /// <remarks>
    /// This method is marked <c>internal</c> to allow unit tests to verify the blackout backoff calculation
    /// independently of the surrounding retry logic.
    /// </remarks>
    internal static int CalculateBlackoutMultiplier(int failCount)
    {
        if (failCount < MaxFailuresBeforeBlackout)
            return 1;

        var exponent = failCount - MaxFailuresBeforeBlackout;
        const int MaxSafeExponent = 30; // Cap exponent to avoid shifting beyond 30 bits (safety) before clamping
        var cappedExp = (int)Math.Min(exponent, MaxSafeExponent);
        var rawMultiplier = 1L << cappedExp;
        var maxMultiplier = MaxBlackoutSeconds / BaseBlackoutSeconds;
        var multiplier = (int)Math.Min(rawMultiplier, (long)maxMultiplier);
        return Math.Max(1, multiplier);
    }

    /// <summary>
    /// Checks if the 401 response indicates an instance-specific auth failure (subStatus 11002).
    /// </summary>
    private async Task<bool> IsInstanceAuthFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            // Limit maximum bytes read to be defensive against large error bodies
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("subStatus", out var subStatusElement))
            {
                if (subStatusElement.TryGetInt32(out var subStatus) && subStatus == 11002)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            // Log at debug level to aid troubleshooting unexpected response formats
            _logger.LogDebug(ex, "Failed to parse 401 response body as JSON");
        }

        return false;
    }

    /// <summary>
    /// Extracts the Retry-After delay from a response if present.
    /// </summary>
    private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        var ra = response.Headers?.RetryAfter;
        if (ra == null) return null;

        if (ra.Delta.HasValue) return ra.Delta;
        if (ra.Date.HasValue)
        {
            var delta = ra.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Gets the current index and increments for next call.
    /// </summary>
    private int GetAndIncrementIndex(InstanceType type)
    {
        if (type == InstanceType.Api)
        {
            return Interlocked.Increment(ref _globalApiIndex) - 1;
        }
        else
        {
            return Interlocked.Increment(ref _globalStreamingIndex) - 1;
        }
    }

}
