namespace octo_fiesta.Services.Common;

/// <summary>
/// Helper class for handling external playlist IDs.
/// Playlist IDs use the format: "pl-{provider}-{externalId}"
/// Example: "pl-deezer-123456", "pl-qobuz-789"
/// </summary>
public static class PlaylistIdHelper
{
    private const string PlaylistPrefix = "pl-";
    
    /// <summary>
    /// Checks if an ID represents an external playlist.
    /// </summary>
    /// <param name="id">The ID to check</param>
    /// <returns>True if the ID starts with "pl-", false otherwise</returns>
    public static bool IsExternalPlaylist(string? id)
    {
        return !string.IsNullOrEmpty(id) && id.StartsWith(PlaylistPrefix, StringComparison.OrdinalIgnoreCase);
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
