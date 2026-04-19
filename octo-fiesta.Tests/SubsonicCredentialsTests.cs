using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Tests;

public class SubsonicCredentialsTests
{
    private static Dictionary<string, string> ValidTokenAuthDictionary() => new()
    {
        ["u"] = "alice",
        ["t"] = "abc123",
        ["s"] = "salt42",
        ["v"] = "1.16.1",
        ["c"] = "aonsoku"
    };

    private static Dictionary<string, string> ValidPasswordAuthDictionary() => new()
    {
        ["u"] = "alice",
        ["p"] = "hunter2",
        ["v"] = "1.16.1",
        ["c"] = "feishin"
    };

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
    public void TryFromDictionary_PasswordAuth_ReturnsCredentials()
    {
        var result = SubsonicCredentials.TryFromDictionary(ValidPasswordAuthDictionary());

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
        Assert.Equal("hunter2", result.Password);
        Assert.Null(result.Token);
        Assert.Null(result.Salt);
        Assert.Equal("1.16.1", result.ApiVersion);
        Assert.Equal("feishin", result.ClientName);
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
    public void TryFromDictionary_TokenWithoutSalt_ReturnsNull()
    {
        var parameters = ValidTokenAuthDictionary();
        parameters.Remove("s");

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.Null(result);
    }
}
