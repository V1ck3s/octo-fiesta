using IOFile = System.IO.File;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Helper class for path building and sanitization.
/// Provides utilities for creating safe file and folder paths for downloaded music files.
/// Always uses Windows-compatible invalid characters since they are a superset of Unix ones.
/// This ensures filenames created in Docker (Linux) are also valid on Windows (e.g. via SMB).
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Characters invalid in Windows file names. This is a superset of Unix invalid chars,
    /// so using these everywhere ensures cross-platform compatibility.
    /// </summary>
    private static readonly char[] InvalidFileNameChars =
    [
        '"', '<', '>', '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31, ':', '*', '?', '\\', '/'
    ];
    
    /// <summary>
    /// Gets the cache directory path for temporary file storage.
    /// Uses system temp directory combined with octo-fiesta-cache subfolder.
    /// Respects TMPDIR environment variable on Linux/macOS.
    /// </summary>
    /// <returns>Full path to the cache directory.</returns>
    public static string GetCachePath()
    {
        return Path.Combine(Path.GetTempPath(), "octo-fiesta-cache");
    }
    
    /// <summary>
    /// Builds the output path for a downloaded track following the Artist/Album/Track structure.
    /// </summary>
    /// <param name="downloadPath">Base download directory path.</param>
    /// <param name="artist">Artist name (will be sanitized).</param>
    /// <param name="album">Album name (will be sanitized).</param>
    /// <param name="title">Track title (will be sanitized).</param>
    /// <param name="trackNumber">Optional track number for prefix.</param>
    /// <param name="extension">File extension (e.g., ".flac", ".mp3").</param>
    /// <returns>Full path for the track file.</returns>
    public static string BuildTrackPath(string downloadPath, string artist, string album, string title, int? trackNumber, string extension)
    {
        var safeArtist = SanitizeFolderName(artist);
        var safeAlbum = SanitizeFolderName(album);
        var safeTitle = SanitizeFileName(title);
        
        var artistFolder = Path.Combine(downloadPath, safeArtist);
        var albumFolder = Path.Combine(artistFolder, safeAlbum);
        
        var trackPrefix = trackNumber.HasValue ? $"{trackNumber:D2} - " : "";
        var fileName = $"{trackPrefix}{safeTitle}{extension}";
        
        return Path.Combine(albumFolder, fileName);
    }

    /// <summary>
    /// Sanitizes a file name by removing invalid characters.
    /// </summary>
    /// <param name="fileName">Original file name.</param>
    /// <returns>Sanitized file name safe for all file systems.</returns>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "Unknown";
        }
        
        var invalidChars = InvalidFileNameChars;
        var sanitized = new string(fileName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100];
        }
        
        return sanitized.Trim();
    }

    /// <summary>
    /// Sanitizes a folder name by removing invalid path characters.
    /// </summary>
    /// <param name="folderName">Original folder name.</param>
    /// <returns>Sanitized folder name safe for all file systems.</returns>
    public static string SanitizeFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "Unknown";
        }
        
        var invalidChars = InvalidFileNameChars;
            
        var sanitized = new string(folderName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        
        // Remove leading/trailing dots and spaces (Windows folder restrictions)
        sanitized = sanitized.Trim().TrimEnd('.');
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100].TrimEnd('.');
        }
        
        // Ensure we have a valid name
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "Unknown";
        }
        
        return sanitized;
    }

    /// <summary>
    /// Resolves a unique file path by appending a counter if the file already exists.
    /// </summary>
    /// <param name="basePath">Desired file path.</param>
    /// <returns>Unique file path that does not exist yet.</returns>
    public static string ResolveUniquePath(string basePath)
    {
        if (!IOFile.Exists(basePath))
        {
            return basePath;
        }
        
        var directory = Path.GetDirectoryName(basePath)!;
        var extension = Path.GetExtension(basePath);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(basePath);
        
        var counter = 1;
        string uniquePath;
        do
        {
            uniquePath = Path.Combine(directory, $"{fileNameWithoutExt} ({counter}){extension}");
            counter++;
        } while (IOFile.Exists(uniquePath));
        
        return uniquePath;
    }
}
