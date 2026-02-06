namespace octo_fiesta.Services.Common;

/// <summary>
/// Helper class for handling external playlist IDs.
/// Playlist IDs use the format: "pl-{provider}-{externalId}"
/// Example: "pl-deezer-123456", "pl-qobuz-789"
/// </summary>
public static class PlaylistIdHelper
{
    private const string PlaylistPrefix = "pl-";
    
    // Known external providers for playlists
    private static readonly string[] KnownProviders = { "deezer", "qobuz", "squidwtf" };
    
    /// <summary>
    /// Checks if an ID represents an external playlist.
    /// Must match format "pl-{provider}-{externalId}" where provider is a known provider.
    /// </summary>
    /// <param name="id">The ID to check</param>
    /// <returns>True if the ID is a valid external playlist ID, false otherwise</returns>
    public static bool IsExternalPlaylist(string? id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith(PlaylistPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        
        // Remove "pl-" prefix and check for known provider
        var withoutPrefix = id.Substring(PlaylistPrefix.Length);
        
        foreach (var provider in KnownProviders)
        {
            if (withoutPrefix.StartsWith(provider + "-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Parses a playlist ID to extract provider and external ID.
    /// </summary>
    /// <param name="id">The playlist ID in format "pl-{provider}-{externalId}"</param>
    /// <returns>A tuple containing (provider, externalId)</returns>
    /// <exception cref="ArgumentException">Thrown if the ID format is invalid</exception>
    public static (string provider, string externalId) ParsePlaylistId(string id)
    {
        if (!IsExternalPlaylist(id))
        {
            throw new ArgumentException($"Invalid playlist ID format. Expected 'pl-{{provider}}-{{externalId}}', got '{id}'", nameof(id));
        }
        
        // Remove "pl-" prefix
        var withoutPrefix = id.Substring(PlaylistPrefix.Length);
        
        // Split by first dash to get provider and externalId
        var dashIndex = withoutPrefix.IndexOf('-');
        if (dashIndex == -1)
        {
            throw new ArgumentException($"Invalid playlist ID format. Expected 'pl-{{provider}}-{{externalId}}', got '{id}'", nameof(id));
        }
        
        var provider = withoutPrefix.Substring(0, dashIndex);
        var externalId = withoutPrefix.Substring(dashIndex + 1);
        
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(externalId))
        {
            throw new ArgumentException($"Invalid playlist ID format. Provider or external ID is empty in '{id}'", nameof(id));
        }
        
        return (provider, externalId);
    }
    
    /// <summary>
    /// Creates a playlist ID from provider and external ID.
    /// </summary>
    /// <param name="provider">The provider name (e.g., "deezer", "qobuz")</param>
    /// <param name="externalId">The external ID from the provider</param>
    /// <returns>A playlist ID in format "pl-{provider}-{externalId}"</returns>
    public static string CreatePlaylistId(string provider, string externalId)
    {
        if (string.IsNullOrEmpty(provider))
        {
            throw new ArgumentException("Provider cannot be null or empty", nameof(provider));
        }
        
        if (string.IsNullOrEmpty(externalId))
        {
            throw new ArgumentException("External ID cannot be null or empty", nameof(externalId));
        }
        
        return $"{PlaylistPrefix}{provider.ToLowerInvariant()}-{externalId}";
    }
}
