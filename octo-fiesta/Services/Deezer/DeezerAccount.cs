using System.Text.Json;
using octo_fiesta.Models.Domain;

namespace octo_fiesta.Services.Deezer;

/// <summary>
/// The best format the Deezer account can stream, read from its user data at startup and on
/// each token refresh.
/// </summary>
public class DeezerAccount
{
    private volatile AudioFormat? _bestFormat;

    /// <summary>
    /// Null until the account has been read.
    /// </summary>
    public AudioFormat? BestFormat => _bestFormat;

    public void Update(JsonElement user) => _bestFormat = BestFormatOf(user);

    /// <summary>
    /// FLAC with lossless streaming, MP3 320 with HQ streaming, MP3 128 otherwise.
    /// </summary>
    public static AudioFormat BestFormatOf(JsonElement user)
    {
        if (!user.TryGetProperty("OPTIONS", out var options))
        {
            return AudioFormat.Mp3_128;
        }
        if (IsTrue(options, "web_lossless"))
        {
            return AudioFormat.Flac();
        }
        return IsTrue(options, "web_hq") ? AudioFormat.Mp3_320 : AudioFormat.Mp3_128;
    }

    private static bool IsTrue(JsonElement options, string name) =>
        options.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
