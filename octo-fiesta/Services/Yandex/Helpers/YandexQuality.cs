using System.Collections.Immutable;

namespace octo_fiesta.Services.Yandex;

public static class YandexQuality
{

    private const string AAC_64 = "AAC_64";
    private const string MP3_128 = "MP3_128";
    private const string MP3_192 = "MP3_192";
    private const string AAC_192 = "AAC_192";
    private const string AAC_256 = "AAC_256";
    private const string MP3_320 = "MP3_320";
    private const string FLAC = "FLAC";

    private static readonly HashSet<string> _validQualities = new(StringComparer.OrdinalIgnoreCase)
    {
        AAC_64,
        MP3_128,
        MP3_192,
        AAC_192,
        AAC_256,
        MP3_320,
        FLAC
    };

    public static List<string> ValidQualities
    {
        get => _validQualities.ToList();
    }

    private static class Codecs
    {
        public static readonly ImmutableList<string> HE_AAC = ["he-aac","he-aac-mp4"];
        public static readonly ImmutableList<string> AAC = ["aac", "aac-mp4"];
        public static readonly ImmutableList<string> MP3 = ["mp3"];
        public static readonly ImmutableList<string> FLAC = ["flac", "flac-mp4"];
    }

    public static bool IsValid(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return true;
        }
        return _validQualities.Contains(quality);
    }

    public static string FromApiParams(string codec, int bitrate)
    {
        codec = codec.Trim().ToLowerInvariant();

        if (codec.StartsWith("flac")) return FLAC;
        if (codec.StartsWith("he-aac")) return AAC_64;

        bool isMp3 = codec == "mp3";
        return bitrate switch
        {
            >= 256 => isMp3 ? MP3_320 : AAC_256,
            >= 192 => isMp3 ? MP3_192 : AAC_192,
            _ => MP3_128,
        };
    }

    public static (string level, List<string> codecs) ToApiParams(string? quality)
    {
        quality = quality?.Trim().ToUpperInvariant();

        string level = quality switch
        {
            MP3_320 or AAC_256 or FLAC => "lossless",
            MP3_192 or AAC_192 => "nq",
            MP3_128 or AAC_64 => "lq",
            _ => "lossless"
        };
        
        var codecs = quality switch
        {
            MP3_320 or MP3_192 or MP3_128 => Codecs.MP3,
            AAC_192 or AAC_256 => Codecs.AAC,
            AAC_64 => Codecs.HE_AAC,
            FLAC => Codecs.FLAC,
            _ => Codecs.FLAC,
        };

        return (level, codecs.ToList());
    }

    public static string CodecToExtension(string codec)
    {
        return codec.Trim().ToLowerInvariant() switch
        {
            "flac" => ".flac",
            "mp3" => ".mp3",
            "aac" or "he-aac" => ".aac",
            _ => ".m4a"
        };
    }

}