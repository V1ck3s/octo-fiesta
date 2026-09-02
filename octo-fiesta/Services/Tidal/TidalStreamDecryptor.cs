using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.IO;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace octo_fiesta.Services.Tidal;

/// <summary>
/// Decrypts the encrypted streams Tidal serves for some tracks. The BTS manifest then
/// carries a keyId, an encrypted security token holding the actual AES key and nonce.
/// </summary>
public static class TidalStreamDecryptor
{
    /// <summary>
    /// Key wrapping every per-track security token. Fixed for the device clients.
    /// </summary>
    private const string MasterKey = "UIlTTEMmmLfGowo/UC60x2H45W6MdGgTRfo/umg4754=";

    /// <summary>
    /// Unwraps a security token into the AES-CTR key and nonce protecting the stream.
    /// </summary>
    public static (byte[] Key, byte[] Nonce) DecryptSecurityToken(string securityToken)
    {
        var masterKey = Convert.FromBase64String(MasterKey);
        var token = Convert.FromBase64String(securityToken);

        if (token.Length <= 16)
        {
            throw new ArgumentException("The Tidal security token is too short to hold an IV and a payload.",
                nameof(securityToken));
        }

        var iv = token[..16];
        var payload = token[16..];

        var cipher = new BufferedBlockCipher(new CbcBlockCipher(new AesEngine()));
        cipher.Init(false, new ParametersWithIV(new KeyParameter(masterKey), iv));

        var decrypted = new byte[cipher.GetOutputSize(payload.Length)];
        var written = cipher.ProcessBytes(payload, 0, payload.Length, decrypted, 0);
        cipher.DoFinal(decrypted, written);

        return (decrypted[..16], decrypted[16..24]);
    }

    /// <summary>
    /// Wraps an encrypted stream so reads come out in clear. AES-CTR with the nonce as the
    /// high half of the counter block and a 64-bit counter starting at zero.
    /// </summary>
    public static Stream Decrypt(Stream encrypted, string securityToken)
    {
        var (key, nonce) = DecryptSecurityToken(securityToken);

        var counter = new byte[16];
        nonce.CopyTo(counter, 0);

        var cipher = new BufferedBlockCipher(new SicBlockCipher(new AesEngine()));
        cipher.Init(false, new ParametersWithIV(new KeyParameter(key), counter));

        return new CipherStream(encrypted, cipher, null);
    }
}
