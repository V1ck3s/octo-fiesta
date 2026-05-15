using System.Text.Json;
using octo_fiesta.Services.SquidWTF;

namespace octo_fiesta.Tests;

public class SquidWTFCaptchaSolverTests
{
    [Fact]
    public void SolveChallenge_FindsKnownCounterAndDerivedKey()
    {
        // Fixture computed externally with the ALTCHA v2 web SHA-256 algorithm
        // (truncate-each-iteration variant). Known answer for these inputs.
        var challengeJson = """
        {
          "algorithm": "SHA-256",
          "cost": 10,
          "keyLength": 16,
          "keyPrefix": "00",
          "nonce": "00112233445566778899aabbccddeeff",
          "salt": "ffeeddccbbaa99887766554433221100"
        }
        """;
        using var doc = JsonDocument.Parse(challengeJson);

        var (counter, derivedKeyHex, _) = SquidWTFCaptchaSolver.SolveChallenge(doc.RootElement, default);

        Assert.Equal(32, counter);
        Assert.Equal("00ea9d136de46c2b84bcf0ec9216f748", derivedKeyHex);
    }
}
