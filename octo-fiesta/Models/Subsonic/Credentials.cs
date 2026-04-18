namespace octo_fiesta.Models.Subsonic;

/// <summary>
/// Represents a credentials set for subsonic login
/// </summary>
 
public record SubsonicCredentials(
    string Username,
    string Token,
    string Salt,
    string ApiVersion,
    string ClientName
)
{
    public static SubsonicCredentials? TryFromDictionary(
        IDictionary<string, string>? dict)
    {
        if (dict == null) return null;
        if (!dict.TryGetValue("u", out var Username) || string.IsNullOrEmpty(Username)) return null;
        if (!dict.TryGetValue("t", out var Token) || string.IsNullOrEmpty(Token)) return null;
        if (!dict.TryGetValue("s", out var Salt) || string.IsNullOrEmpty(Salt)) return null;
        if (!dict.TryGetValue("v", out var ApiVersion) || string.IsNullOrEmpty(ApiVersion)) return null;
        if (!dict.TryGetValue("c", out var ClientName) || string.IsNullOrEmpty(ClientName)) return null;

        return new SubsonicCredentials(Username, Token, Salt, ApiVersion, ClientName);
    }

}