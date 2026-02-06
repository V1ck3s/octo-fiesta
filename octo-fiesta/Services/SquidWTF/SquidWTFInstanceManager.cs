using System.Text.Json;
using System.Text.Json.Serialization;
using octo_fiesta.Models.Settings;
using Microsoft.Extensions.Options;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Manages SquidWTF API instances with automatic failover
/// Fetches available instances from remote JSON and switches to the next one if timeout occurs
/// </summary>
public class SquidWTFInstanceManager
{
    private readonly HttpClient _httpClient;
    private readonly SquidWTFSettings _settings;
    private readonly ILogger<SquidWTFInstanceManager> _logger;
    
    private const string InstancesJsonUrl = "https://raw.githubusercontent.com/SamidyFR/monochrome/main/public/instances.json";
    private const int DefaultTimeoutSeconds = 5;
    
    // Static Qobuz API (no failover needed as there's only one)
    private const string QobuzBaseUrl = "https://qobuz.squid.wtf";
    
    // Instance state - shared across all requests during app lifetime
    private List<string>? _tidalInstances;
    private int _currentInstanceIndex;
    private string? _currentTidalInstance;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    
    public int TimeoutSeconds { get; }
    
    public SquidWTFInstanceManager(
        IHttpClientFactory httpClientFactory,
        IOptions<SquidWTFSettings> settings,
        ILogger<SquidWTFInstanceManager> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _logger = logger;
        
        // Use configured timeout or default
        TimeoutSeconds = _settings.InstanceTimeoutSeconds > 0 
            ? _settings.InstanceTimeoutSeconds 
            : DefaultTimeoutSeconds;
    }
    
    /// <summary>
    /// Gets the base URL for the current source (Qobuz or Tidal)
    /// For Tidal, returns the currently active instance
    /// </summary>
    public async Task<string> GetBaseUrlAsync()
    {
        if (_settings.Source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase))
        {
            return QobuzBaseUrl;
        }
        
        await EnsureInitializedAsync();
        return _currentTidalInstance ?? throw new InvalidOperationException("No Tidal instance available");
    }
    
    /// <summary>
    /// Sends an HTTP request with automatic failover to next instance on timeout
    /// </summary>
    public async Task<HttpResponseMessage> SendWithFailoverAsync(
        Func<string, HttpRequestMessage> createRequest,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = await GetBaseUrlAsync();
        
        // For Qobuz, just send the request (no failover)
        if (_settings.Source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase))
        {
            var request = createRequest(baseUrl);
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        
        // For Tidal, try with failover
        var attemptedInstances = new HashSet<string>();
        
        while (attemptedInstances.Count < (_tidalInstances?.Count ?? 1))
        {
            var currentUrl = _currentTidalInstance!;
            attemptedInstances.Add(currentUrl);
            
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                
                var request = createRequest(currentUrl);
                var response = await _httpClient.SendAsync(request, cts.Token);
                
                // Success - this instance works
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout - switch to next instance
                _logger.LogWarning("Tidal instance {Instance} timed out after {Timeout}s, switching to next...", 
                    currentUrl, TimeoutSeconds);
                SwitchToNextInstance();
            }
            catch (HttpRequestException ex)
            {
                // Network error - switch to next instance
                _logger.LogWarning(ex, "Tidal instance {Instance} failed, switching to next...", currentUrl);
                SwitchToNextInstance();
            }
        }
        
        throw new InvalidOperationException("All Tidal instances failed or timed out");
    }
    
    /// <summary>
    /// Marks the current instance as slow/failed and switches to the next one
    /// </summary>
    public void SwitchToNextInstance()
    {
        if (_tidalInstances == null || _tidalInstances.Count <= 1)
        {
            _logger.LogWarning("Cannot switch instance: only one instance available");
            return;
        }
        
        var previousInstance = _currentTidalInstance;
        _currentInstanceIndex = (_currentInstanceIndex + 1) % _tidalInstances.Count;
        _currentTidalInstance = _tidalInstances[_currentInstanceIndex];
        
        _logger.LogInformation("Switched Tidal instance from {Previous} to {Current}", 
            previousInstance, _currentTidalInstance);
    }
    
    /// <summary>
    /// Gets the currently active Tidal instance URL (for logging/debugging)
    /// </summary>
    public string? GetCurrentInstance() => _currentTidalInstance;
    
    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            
            await LoadInstancesAsync();
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
    
    private async Task LoadInstancesAsync()
    {
        try
        {
            _logger.LogInformation("Loading SquidWTF instances from {Url}", InstancesJsonUrl);
            
            var response = await _httpClient.GetStringAsync(InstancesJsonUrl);
            var instances = JsonSerializer.Deserialize<InstancesJson>(response);
            
            if (instances?.Api == null || instances.Api.Count == 0)
            {
                throw new InvalidOperationException("No API instances found in instances.json");
            }
            
            // Normalize URLs (remove trailing slashes)
            _tidalInstances = instances.Api
                .Select(url => url.TrimEnd('/'))
                .ToList();
            
            _currentInstanceIndex = 0;
            _currentTidalInstance = _tidalInstances[0];
            
            _logger.LogInformation("Loaded {Count} Tidal instances, starting with {Instance}", 
                _tidalInstances.Count, _currentTidalInstance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load instances from remote JSON, using fallback");
            
            // Fallback to hardcoded instance
            _tidalInstances = new List<string> { "https://tidal-api.binimum.org" };
            _currentInstanceIndex = 0;
            _currentTidalInstance = _tidalInstances[0];
        }
    }
    
    private class InstancesJson
    {
        [JsonPropertyName("api")]
        public List<string>? Api { get; set; }
        
        [JsonPropertyName("streaming")]
        public List<string>? Streaming { get; set; }
    }
}
