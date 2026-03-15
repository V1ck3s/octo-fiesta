namespace octo_fiesta.Services.Common;

/// <summary>
/// Helper class for audio quality comparison
/// </summary>
public static class QualityHelper
{
    /// <summary>
    /// Quality hierarchy from lowest to highest
    /// </summary>
    private static readonly Dictionary<string, int> QualityLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        // Lossy qualities (from lowest to highest)
        { "AAC_64", 0 },
        { "AAC_96", 1 },    // Tidal LOW - 96kbps AAC
        { "MP3_128", 2 },
        { "MP3_192", 3 },
        { "AAC_192", 4 },
        { "AAC_256", 5 },
        { "AAC_320", 6 },   // Tidal HIGH - 320kbps AAC
        { "MP3_320", 6 },
        
        // Lossless qualities (Qobuz/Tidal variants)
        { "FLAC", 7 },
        { "FLAC_16", 7 },
        { "FLAC_24", 8 },   // Tidal HI_RES_LOSSLESS
        { "FLAC_24_LOW", 8 },
        { "FLAC_24_HIGH", 9 }
    };




    /// <summary>
    /// Determines if a track should be upgraded based on stored quality vs target quality
    /// </summary>
    /// <param name="existingQuality">Quality of the existing file (null for legacy downloads)</param>
    /// <param name="targetQuality">Target quality setting</param>
    /// <returns>True if upgrade is needed</returns>
    public static bool ShouldUpgrade(string? existingQuality, string? targetQuality)
    {
        if (string.IsNullOrEmpty(targetQuality))
            return false;
        
        // Legacy downloads without quality info should always be re-downloaded
        if (string.IsNullOrEmpty(existingQuality))
            return true;
        
        var existingLevel = GetQualityLevel(existingQuality);
        var targetLevel = GetQualityLevel(targetQuality);
        
        return targetLevel > existingLevel;
    }
    
    /// <summary>
    /// Gets the numeric quality level for comparison
    /// </summary>
    public static int GetQualityLevel(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
            return 0;
        
        return QualityLevels.TryGetValue(quality, out var level) ? level : 0;
    }
}
