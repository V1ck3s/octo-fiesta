namespace octo_fiesta.Services.Common;

/// <summary>
/// Helper class for audio quality comparison
/// </summary>
public static class QualityHelper
{
    /// <summary>
    /// Determines if a file should be upgraded based on its extension and target quality
    /// </summary>
    /// <param name="existingFilePath">Path to the existing file</param>
    /// <param name="targetQuality">Target quality setting (e.g., "FLAC", "MP3_320")</param>
    /// <returns>True if the existing file should be replaced with higher quality</returns>
    public static bool ShouldUpgrade(string existingFilePath, string? targetQuality)
    {
        if (string.IsNullOrEmpty(targetQuality))
            return false;
        
        var extension = Path.GetExtension(existingFilePath).ToLowerInvariant();
        var isExistingFlac = extension == ".flac";
        var isTargetFlac = targetQuality.Contains("FLAC", StringComparison.OrdinalIgnoreCase);
        
        // Only upgrade if existing is MP3 and target is FLAC
        return !isExistingFlac && isTargetFlac;
    }
}
