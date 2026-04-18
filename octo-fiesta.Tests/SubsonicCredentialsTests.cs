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

    [Fact]
    public void TryFromDictionary_ValidParameters_ReturnsCredentials()
    {
        var parameters = ValidTokenAuthDictionary();

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
        Assert.Equal("abc123", result.Token);
        Assert.Equal("salt42", result.Salt);
        Assert.Equal("1.16.1", result.ApiVersion);
        Assert.Equal("aonsoku", result.ClientName);
    }

    [Theory]
    [InlineData("u")]
    [InlineData("t")]
    [InlineData("s")]
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
    [InlineData("t")]
    [InlineData("s")]
    [InlineData("v")]
    [InlineData("c")]
    public void TryFromDictionary_EmptyRequiredParameter_ReturnsNull(string keyToRemove)
    {
        var parameters = ValidTokenAuthDictionary();
        parameters[keyToRemove] = "";

        var result = SubsonicCredentials.TryFromDictionary(parameters);

        Assert.Null(result);
    }
}