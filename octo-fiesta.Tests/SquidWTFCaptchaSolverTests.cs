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

    [Fact]
    public void SolveChallenge_Pbkdf2Variant_FindsKnownCounterAndDerivedKey()
    {
        // Fixture computed externally with PBKDF2/SHA-256 (amz.squid.wtf variant).
        // password = nonce_bytes ++ big-endian uint32 counter; one PBKDF2 iteration.
        // Python: hashlib.pbkdf2_hmac('sha256', bytes.fromhex('aabbccdd')+struct.pack('>I',116),
        //         bytes.fromhex('11223344'), 1, dklen=16).hex()  → '006cad77ec7d0eaa5b26c8670098258b'
        var challengeJson = """
        {
          "algorithm": "PBKDF2/SHA-256",
          "cost": 1,
          "keyLength": 16,
          "keyPrefix": "00",
          "nonce": "aabbccdd",
          "salt": "11223344"
        }
        """;
        using var doc = JsonDocument.Parse(challengeJson);

        var (counter, derivedKeyHex, _) = SquidWTFCaptchaSolver.SolveChallenge(doc.RootElement, default);

        Assert.Equal(116, counter);
        Assert.Equal("006cad77ec7d0eaa5b26c8670098258b", derivedKeyHex);
    }
}
