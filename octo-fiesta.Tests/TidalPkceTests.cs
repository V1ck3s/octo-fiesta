using System.Security.Cryptography;
using System.Text;
using octo_fiesta.Services.Tidal;

namespace octo_fiesta.Tests;

public class TidalPkceTests
{
    [Fact]
    public void CreateChallenge_DerivesTheChallengeFromTheVerifier()
    {
        var challenge = TidalPkce.CreateChallenge();

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(challenge.CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(expected, challenge.CodeChallenge);
    }

    [Fact]
    public void CreateChallenge_IsSingleUse()
    {
        Assert.NotEqual(TidalPkce.CreateChallenge().CodeVerifier, TidalPkce.CreateChallenge().CodeVerifier);
    }

    [Fact]
    public void BuildAuthorizationUrl_CarriesWhatTidalExpects()
    {
        var challenge = TidalPkce.CreateChallenge();

        var url = TidalPkce.BuildAuthorizationUrl("theClientId", challenge);

        Assert.StartsWith($"{TidalPkce.AuthorizeUrl}?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=theClientId", url);
        Assert.Contains($"client_unique_key={challenge.ClientUniqueKey}", url);
        Assert.Contains($"code_challenge={challenge.CodeChallenge}", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Ftidal.com%2Flogin%2Fauth", url);
        // The scope travels with '+' for the space, the way the web client sends it.
        Assert.Contains("scope=r_usr+w_usr", url);
    }

    [Fact]
    public void ExtractAuthorizationCode_ReadsTheCodeFromTheRedirect()
    {
        var code = TidalPkce.ExtractAuthorizationCode(
            "https://tidal.com/login/auth?code=eyJhbGciOiJF.abc-def_123&state=na");

        Assert.Equal("eyJhbGciOiJF.abc-def_123", code);
    }

    [Fact]
    public void ExtractAuthorizationCode_AcceptsABareCode()
    {
        Assert.Equal("abc123", TidalPkce.ExtractAuthorizationCode("  abc123  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://tidal.com/login/auth")]
    [InlineData("https://tidal.com/login/auth?state=na")]
    public void ExtractAuthorizationCode_WithoutACode_ReturnsNull(string? redirected)
    {
        Assert.Null(TidalPkce.ExtractAuthorizationCode(redirected));
    }

    [Fact]
    public void ExtractAuthorizationCode_SurfacesTheRefusal()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TidalPkce.ExtractAuthorizationCode(
            "https://tidal.com/login/auth?error=access_denied&error_description=User+said+no"));

        Assert.Contains("access_denied", exception.Message);
        Assert.Contains("User said no", exception.Message);
    }
}
