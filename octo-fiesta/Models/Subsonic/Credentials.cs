namespace octo_fiesta.Models.Subsonic;

/// <summary>
/// Represents a credentials set for subsonic login
/// </summary>
public record SubsonicCredentials(
    string Username,
    string? Token,
    string? Salt,
    string? Password,
    string ApiVersion,
    string ClientName
)
{
    public static SubsonicCredentials? TryFromDictionary(IDictionary<string, string>? dict)
    {
        if (dict == null) return null;
        if (!dict.TryGetValue("u", out var username) || string.IsNullOrEmpty(username)) return null;
        if (!dict.TryGetValue("v", out var apiVersion) || string.IsNullOrEmpty(apiVersion)) return null;
        if (!dict.TryGetValue("c", out var clientName) || string.IsNullOrEmpty(clientName)) return null;

        dict.TryGetValue("t", out var token);
        dict.TryGetValue("s", out var salt);
        dict.TryGetValue("p", out var password);

        var hasToken = !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(salt);
        var hasPassword = !string.IsNullOrEmpty(password);
        if (!hasToken && !hasPassword) return null;

        return new SubsonicCredentials(username, token, salt, password, apiVersion, clientName);
    }
}
