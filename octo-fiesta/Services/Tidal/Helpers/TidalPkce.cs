using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace octo_fiesta.Services.Tidal;

/// <summary>
/// One-shot PKCE material. A public client has no secret, so the verifier is what binds
/// the code exchange to the browser login that produced the code.
/// </summary>
public sealed record TidalPkceChallenge(string CodeVerifier, string CodeChallenge, string ClientUniqueKey);

/// <summary>
/// Authorization code flow with PKCE, the login the Tidal web client uses. Device
/// authorization is not an option for it: Tidal answers "Client is not a Limited Input
/// Device client". The web client is the only one found entitled to the LOSSLESS tier,
/// the limited input ones cap at HIGH and hand out AAC where FLAC was asked for.
/// </summary>
public static class TidalPkce
{
    public const string AuthorizeUrl = "https://login.tidal.com/authorize";

    /// <summary>
    /// Redirect registered for the web client. Nothing listens on it, the user copies the
    /// address back from the browser, so it only has to match what Tidal has on file.
    /// </summary>
    public const string RedirectUri = "https://tidal.com/login/auth";

    /// <summary>
    /// Scope granted to the web client. Narrower than the device flow one, which also
    /// asks for w_sub.
    /// </summary>
    public const string Scope = "r_usr w_usr";

    public static TidalPkceChallenge CreateChallenge()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return new TidalPkceChallenge(verifier, challenge, Guid.NewGuid().ToString());
    }

    public static string BuildAuthorizationUrl(string clientId, TidalPkceChallenge challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["client_unique_key"] = challenge.ClientUniqueKey,
            ["code_challenge"] = challenge.CodeChallenge,
            ["code_challenge_method"] = "S256",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["appMode"] = "WEB"
        };

        return $"{AuthorizeUrl}?{string.Join('&', query.Select(p => $"{p.Key}={Encode(p.Value)}"))}";
    }

    /// <summary>
    /// Reads the authorization code out of the address the browser was redirected to.
    /// A bare code is accepted as well, because copying a whole address back into a
    /// terminal is easy to get wrong.
    /// </summary>
    /// <exception cref="InvalidOperationException">The redirect carries an error instead of a code.</exception>
    public static string? ExtractAuthorizationCode(string? redirected)
    {
        if (string.IsNullOrWhiteSpace(redirected))
        {
            return null;
        }

        var value = redirected.Trim();
        if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var queryStart = value.IndexOf('?');
        if (queryStart < 0)
        {
            return null;
        }

        var parameters = ParseQuery(value[(queryStart + 1)..]);

        if (parameters.TryGetValue("error", out var error))
        {
            var description = parameters.GetValueOrDefault("error_description");
            throw new InvalidOperationException(
                $"Tidal refused the login: {error}"
                + (description is null ? "" : $" ({description})"));
        }

        return parameters.GetValueOrDefault("code");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            parameters[key] = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return parameters;
    }

    /// <summary>
    /// Spaces travel as '+' in the authorization URL, the way Tidal's own web client
    /// sends the scope.
    /// </summary>
    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%20", "+");
}
