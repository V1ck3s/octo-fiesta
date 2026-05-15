using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicCredentialsTests
{
    private static Dictionary<string, string> ValidAllAuthDictionary() => new()
    {
        ["u"] = "alice",
        ["t"] = "abc123",
        ["s"] = "salt42",
        ["p"] = "hunter2",
        ["v"] = "1.16.1",
        ["c"] = "aonsoku"
    };

    private static Dictionary<string, string> ValidTokenAuthDictionary()
    {
        var dict = ValidAllAuthDictionary();
        dict.Remove("p");
        return dict;
    }

    private static Dictionary<string, string> ValidPasswordAuthDictionary()
    {
        var dict = ValidAllAuthDictionary();
        dict.Remove("t");
        dict.Remove("s");
        return dict;
    }

    [Fact]
    public void TryFromDictionary_AllParameters_KeepsAllWithoutEnrichment()
    {
        var result = SubsonicCredentials.TryFromDictionary(ValidAllAuthDictionary());

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
        Assert.Equal("abc123", result.Token);
        Assert.Equal("salt42", result.Salt);
        Assert.Equal("hunter2", result.Password);
        Assert.Equal("1.16.1", result.ApiVersion);
        Assert.Equal("aonsoku", result.ClientName);
    }

    [Fact]
    public void TryFromDictionary_TokenAuth_ReturnsCredentials()
    {
        var result = SubsonicCredentials.TryFromDictionary(ValidTokenAuthDictionary());

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
        Assert.Equal("abc123", result.Token);
        Assert.Equal("salt42", result.Salt);
        Assert.Null(result.Password);
        Assert.Equal("1.16.1", result.ApiVersion);
        Assert.Equal("aonsoku", result.ClientName);
    }

    [Fact]
    public void TryFromDictionary_PasswordAuth_ReturnsCredentialsEnrichedWithGeneratedTokenAndSalt()
    {
        var result = SubsonicCredentials.TryFromDictionary(ValidPasswordAuthDictionary());

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
        Assert.Equal("hunter2", result.Password);
        Assert.NotNull(result.Token);
        Assert.NotNull(result.Salt);
        Assert.Equal("1.16.1", result.ApiVersion);
        Assert.Equal("aonsoku", result.ClientName);

        var expectedToken = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("hunter2" + result.Salt))).ToLowerInvariant();
        Assert.Equal(expectedToken, result.Token);
    }

    [Theory]
    [InlineData("u")]
    [InlineData("v")]
    [InlineData("c")]
    public void TryFromDictionary_MissingRequiredParameter_ReturnsNull(string keyToRemove)
    {
        var parameters = ValidTokenAuthDictionary();
        parameters.Remove(keyToRemove);

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("u")]
    [InlineData("v")]
    [InlineData("c")]
    public void TryFromDictionary_EmptyRequiredParameter_ReturnsNull(string keyToRemove)
    {
        var parameters = ValidTokenAuthDictionary();
        parameters[keyToRemove] = "";

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("u")]
    [InlineData("v")]
    [InlineData("c")]
    public void TryFromDictionary_OnlyWhiteSpaceInRequiredParameter_ReturnsNull(string keyToRemove)
    {
        var parameters = ValidTokenAuthDictionary();
        parameters[keyToRemove] = "   ";

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.Null(result);
    }

    [Fact]
    public void TryFromDictionary_NoAuthMethod_ReturnsNull()
    {
        var parameters = new Dictionary<string, string>
        {
            ["u"] = "alice",
            ["v"] = "1.16.1",
            ["c"] = "client"
        };

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.Null(result);
    }

    [Fact]
    public void TryFromDictionary_TokenAndPasswordWithoutSalt_ReturnsCredentialsEnrichedWithGeneratedSaltAndNewToken()
    {
        var parameters = ValidAllAuthDictionary();
        parameters.Remove("s");

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
        Assert.Equal("hunter2", result.Password);
        Assert.NotNull(result.Token);
        Assert.NotEqual(parameters["t"], result.Token);
        Assert.NotNull(result.Salt);
        Assert.Equal("1.16.1", result.ApiVersion);
        Assert.Equal("aonsoku", result.ClientName);

        var expectedToken = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("hunter2" + result.Salt))).ToLowerInvariant();
        Assert.Equal(expectedToken, result.Token);
    }

    [Fact]
    public void TryFromDictionary_TokenWithoutSaltAndPassword_ReturnsNull()
    {
        var parameters = ValidTokenAuthDictionary();
        parameters.Remove("s");

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.Null(result);
    }

    [Fact]
    public void TryFromDictionary_SaltWithoutTokenAndPassword_ReturnsNull()
    {
        var parameters = ValidTokenAuthDictionary();
        parameters.Remove("t");

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.Null(result);
    }

    [Fact]
    public void TryFromDictionary_PasswordAuth_GeneratesDifferentSaltsAcrossCalls()
    {
        var result1 = SubsonicCredentials.TryFromDictionary(ValidPasswordAuthDictionary());
        var result2 = SubsonicCredentials.TryFromDictionary(ValidPasswordAuthDictionary());

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotEqual(result1.Salt, result2.Salt);
    }

    [Fact]
    public void TryFromDictionary_NullDict_ReturnsNull()
    {
        var result = SubsonicCredentials.TryFromDictionary(null);

        Assert.Null(result);
    }
}

