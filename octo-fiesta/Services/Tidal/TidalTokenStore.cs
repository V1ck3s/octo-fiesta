using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.Tidal;

/// <summary>
/// Tokens as persisted on disk. The refresh token is the durable one; the access token
/// is rewritten on every renewal.
/// </summary>
public class TidalTokens
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }

    /// <summary>
    /// Client the tokens were issued to. Tokens are bound to it, so a stored pair from a
    /// different client has to be discarded rather than produce a confusing rejection.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }
}

/// <summary>
/// Reads and writes the Tidal token file. A read-only or unwritable store is not fatal at
/// startup but stops renewals from surviving a restart, so failures are surfaced to the caller.
/// </summary>
public class TidalTokenStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<TidalTokenStore> _logger;

    public TidalTokenStore(IOptions<TidalSettings> settings, ILogger<TidalTokenStore> logger)
    {
        Path = string.IsNullOrWhiteSpace(settings.Value.TokenStore)
            ? "./tidal-tokens.json"
            : settings.Value.TokenStore;
        _logger = logger;
    }

    public string Path { get; }

    public TidalTokens? Load()
    {
        if (!IOFile.Exists(Path))
        {
            return null;
        }

        try
        {
            var json = IOFile.ReadAllText(Path);
            return JsonSerializer.Deserialize<TidalTokens>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the Tidal token store at {Path}", Path);
            return null;
        }
    }

    public async Task SaveAsync(TidalTokens tokens, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(tokens, SerializerOptions);
            await IOFile.WriteAllTextAsync(Path, json, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
