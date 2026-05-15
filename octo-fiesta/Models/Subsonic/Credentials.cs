using System.Security.Cryptography;
using System.Text;

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
        if (!dict.TryGetValue("u", out var username) || string.IsNullOrWhiteSpace(username)) return null;
        if (!dict.TryGetValue("v", out var apiVersion) || string.IsNullOrWhiteSpace(apiVersion)) return null;
        if (!dict.TryGetValue("c", out var clientName) || string.IsNullOrWhiteSpace(clientName)) return null;

        dict.TryGetValue("t", out var token);
        dict.TryGetValue("s", out var salt);
        dict.TryGetValue("p", out var password);

        var hasToken = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(salt);
        var hasPassword = !string.IsNullOrWhiteSpace(password);
        if (!hasToken && !hasPassword) return null;
        if (!hasToken)
        {
            salt = GenerateSalt();
            token = ComputeTokenFromPassword(password!, salt);
        }

        return new SubsonicCredentials(username, token, salt, password, apiVersion, clientName);
    }

    private static string ComputeTokenFromPassword(
        string password,
        string salt
        )
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(password + salt));
        var token = Convert.ToHexString(bytes).ToLowerInvariant();

        return token;
    }

    private static string GenerateSalt()
    {
        var saltBytes = RandomNumberGenerator.GetBytes(8);
        var salt = Convert.ToHexString(saltBytes).ToLowerInvariant();
        return salt;
    }
}

