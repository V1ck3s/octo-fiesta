using System.Text.RegularExpressions;

namespace octo_fiesta.Services.Qobuz;

/// <summary>
/// Service to dynamically extract Qobuz App ID and secrets from the Qobuz web player
/// This is necessary because these values change periodically
/// Based on the Python qobuz-dl implementation
/// </summary>
public class QobuzBundleService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QobuzBundleService> _logger;
    
    private const string BaseUrl = "https://play.qobuz.com";
    private const string LoginPageUrl = "https://play.qobuz.com/login";
    
    // Regex patterns to extract bundle URL and App ID
    private static readonly Regex BundleUrlRegex = new(
        @"<script src=""(/resources/\d+\.\d+\.\d+-[a-z]\d{3}/bundle\.js)""></script>",
        RegexOptions.Compiled);
    
    private static readonly Regex AppIdRegex = new(
        @"production:\{api:\{appId:""(?<app_id>\d{9})"",appSecret:""\w{32}""",
        RegexOptions.Compiled);
    
    // Cached values (valid for the lifetime of the application)
    private string? _cachedAppId;
    private List<string>? _cachedSecrets;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public QobuzBundleService(IHttpClientFactory httpClientFactory, ILogger<QobuzBundleService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
        _logger = logger;
    }

    /// <summary>
    /// Gets the Qobuz App ID, extracting it from the bundle if not cached
    /// </summary>
    public virtual async Task<string> GetAppIdAsync()
    {
        await EnsureInitializedAsync();
        return _cachedAppId!;
    }

    /// <summary>
    /// Gets the Qobuz secrets list, extracting them from the bundle if not cached
    /// </summary>
    public virtual async Task<List<string>> GetSecretsAsync()
    {
        await EnsureInitializedAsync();
        return _cachedSecrets!;
    }

    /// <summary>
    /// Gets a specific secret by index (used for signing requests)
    /// </summary>
    public virtual async Task<string> GetSecretAsync(int index = 0)
    {
        var secrets = await GetSecretsAsync();
        if (index < 0 || index >= secrets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), 
                $"Secret index {index} out of range (0-{secrets.Count - 1})");
        }
        return secrets[index];
    }

    /// <summary>
    /// Ensures App ID and secrets are extracted and cached
    /// </summary>
    private async Task EnsureInitializedAsync()
    {
        if (_cachedAppId != null && _cachedSecrets != null)
        {
            return;
        }

        await _initLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cachedAppId != null && _cachedSecrets != null)
            {
                return;
            }

            _logger.LogInformation("Extracting Qobuz App ID and secrets from web bundle...");

            // Step 1: Get the bundle URL from login page
            var bundleUrl = await GetBundleUrlAsync();
            _logger.LogInformation("Found bundle URL: {BundleUrl}", bundleUrl);

            // Step 2: Download the bundle JavaScript
            var bundleJs = await DownloadBundleAsync(bundleUrl);

            // Step 3: Extract App ID
            _cachedAppId = ExtractAppId(bundleJs);
            _logger.LogInformation("Extracted App ID: {AppId}", _cachedAppId);

            // Step 4: Extract secrets (they are base64 encoded in the bundle)
            _cachedSecrets = ExtractSecrets(bundleJs);
            _logger.LogInformation("Extracted {Count} secrets", _cachedSecrets.Count);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Gets the bundle JavaScript URL from the login page
    /// </summary>
    private async Task<string> GetBundleUrlAsync()
    {
        var response = await _httpClient.GetAsync(LoginPageUrl);
        response.EnsureSuccessStatusCode();
        
        var html = await response.Content.ReadAsStringAsync();
        var match = BundleUrlRegex.Match(html);
        
        if (!match.Success)
        {
            throw new Exception("Could not find bundle URL in Qobuz login page");
        }

        return BaseUrl + match.Groups[1].Value;
    }

    /// <summary>
    /// Downloads the bundle JavaScript file
    /// </summary>
    private async Task<string> DownloadBundleAsync(string bundleUrl)
    {
        var response = await _httpClient.GetAsync(bundleUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Extracts the App ID from the bundle JavaScript
    /// </summary>
    private string ExtractAppId(string bundleJs)
    {
        var match = AppIdRegex.Match(bundleJs);
        
        if (!match.Success)
        {
            throw new Exception("Could not extract App ID from bundle");
        }

        return match.Groups["app_id"].Value;
    }

    /// <summary>
    /// Extracts the secrets from the bundle JavaScript
    /// Based on the Python qobuz-dl implementation (bundle.py)
    /// The secrets are composed of seed, info, and extras base64-encoded strings
    /// </summary>
    private List<string> ExtractSecrets(string bundleJs)
    {
        var secrets = new Dictionary<string, List<string>>();
        
        // Step 1: Extract seed and timezone pairs
        // Pattern: [a-z].initialSeed("base64string",window.utimezone.timezone)
        var seedTimezonePattern = new Regex(
            @"[a-z]\.initialSeed\(""(?<seed>[\w=]+)"",window\.utimezone\.(?<timezone>[a-z]+)\)",
            RegexOptions.IgnoreCase);
        
        var seedMatches = seedTimezonePattern.Matches(bundleJs);
        
        foreach (Match match in seedMatches)
        {
            var seed = match.Groups["seed"].Value;
            var timezone = match.Groups["timezone"].Value.ToLower();
            
            if (!secrets.ContainsKey(timezone))
            {
                secrets[timezone] = new List<string>();
            }
            secrets[timezone].Add(seed);
        }

        if (secrets.Count == 0)
        {
            throw new Exception("Could not extract seed/timezone pairs from bundle");
        }

        // Step 2: Reorder secrets (move second item to first, as per Python implementation)
        var keypairs = secrets.ToList();
        if (keypairs.Count > 1)
        {
            var secondItem = keypairs[1];
            secrets.Remove(secondItem.Key);
            var newDict = new Dictionary<string, List<string>> { { secondItem.Key, secondItem.Value } };
            foreach (var kv in keypairs)
            {
                if (kv.Key != secondItem.Key)
                {
                    newDict[kv.Key] = kv.Value;
                }
            }
            secrets = newDict;
        }

        // Step 3: Extract info and extras for each timezone
        // Pattern: name:"\w+/(Timezone)",info:"base64",extras:"base64"
        var timezones = string.Join("|", secrets.Keys.Select(tz => 
            char.ToUpper(tz[0]) + tz.Substring(1)));
        
        var infoExtrasPattern = new Regex(
            $@"name:""\w+/(?<timezone>{timezones})"",info:""(?<info>[\w=]+)"",extras:""(?<extras>[\w=]+)""",
            RegexOptions.IgnoreCase);
        
        var infoExtrasMatches = infoExtrasPattern.Matches(bundleJs);
        
        foreach (Match match in infoExtrasMatches)
        {
            var timezone = match.Groups["timezone"].Value.ToLower();
            var info = match.Groups["info"].Value;
            var extras = match.Groups["extras"].Value;
            
            if (secrets.ContainsKey(timezone))
            {
                secrets[timezone].Add(info);
                secrets[timezone].Add(extras);
            }
        }

        // Step 4: Decode the secrets
        // Concatenate all base64 strings for each timezone, remove last 44 chars, then decode
        var decodedSecrets = new List<string>();
        
        foreach (var kvp in secrets)
        {
            var concatenated = string.Join("", kvp.Value);
            
            // Remove last 44 characters as per Python implementation
            if (concatenated.Length > 44)
            {
                concatenated = concatenated.Substring(0, concatenated.Length - 44);
            }
            
            try
            {
                var bytes = Convert.FromBase64String(concatenated);
                var decoded = System.Text.Encoding.UTF8.GetString(bytes);
                decodedSecrets.Add(decoded);
                _logger.LogDebug("Decoded secret for timezone {Timezone}: {Length} chars", kvp.Key, decoded.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decode secret for timezone {Timezone}", kvp.Key);
            }
        }

        if (decodedSecrets.Count == 0)
        {
            throw new Exception("Could not decode any secrets from bundle");
        }

        return decodedSecrets;
    }

    /// <summary>
    /// Tries to decode a base64 string
    /// </summary>
    private bool TryDecodeBase64(string input, out string decoded)
    {
        decoded = string.Empty;
        
        try
        {
            var bytes = Convert.FromBase64String(input);
            decoded = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
