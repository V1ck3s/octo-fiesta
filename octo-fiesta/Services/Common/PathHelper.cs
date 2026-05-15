using octo_fiesta.Models.Domain;
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
    /// Builds the output path for a downloaded track using a configurable folder template.
    /// The template is split on '/' — segments before the last become folders (sanitized via
    /// <see cref="SanitizeFolderName"/>), the last segment becomes the file name (sanitized via
    /// <see cref="SanitizeFileName"/>). The file extension is appended automatically.
    /// </summary>
    /// <param name="downloadPath">Base download directory path.</param>
    /// <param name="song">Song metadata providing all placeholder values.</param>
    /// <param name="extension">File extension including the dot (e.g., ".flac", ".mp3").</param>
    /// <param name="template">Folder template with {placeholder} tokens. Uses '/' to separate folder levels.</param>
    /// <param name="downloadedQuality">Quality string for {quality} placeholder (e.g., "FLAC", "MP3_320").</param>
    /// <returns>Full path for the track file.</returns>
    public static string BuildTrackPath(string downloadPath, Song song, string extension, string template, string? downloadedQuality)
    {
        var artistForPath = song.AlbumArtist ?? song.Artist;

        var segments = template.Split('/');
        var result = downloadPath;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = ReplacePlaceholders(segments[i], song, artistForPath, downloadedQuality);
            var isFileName = i == segments.Length - 1;

            if (isFileName)
            {
                var safeFileName = SanitizeFileName(segment);
                result = Path.Combine(result, $"{safeFileName}{extension}");
            }
            else
            {
                var safeFolder = SanitizeFolderName(segment);
                result = Path.Combine(result, safeFolder);
            }
        }

        return result;
    }

    /// <summary>
    /// Replaces template placeholders with actual metadata values.
    /// </summary>
    internal static string ReplacePlaceholders(string segment, Song song, string artistForPath, string? downloadedQuality)
    {
        var result = segment
            .Replace("{artist}", artistForPath)
            .Replace("{album}", song.Album)
            .Replace("{title}", song.Title);

        // {track} — zero-padded track number, empty string if null
        var trackValue = song.Track.HasValue ? $"{song.Track.Value:D2}" : "";
        result = result.Replace("{track}", trackValue);

        // {disc} — disc number, "Unknown" if null
        var discValue = song.DiscNumber.HasValue ? song.DiscNumber.Value.ToString() : "Unknown";
        result = result.Replace("{disc}", discValue);

        // {year} — year, "Unknown" if null
        var yearValue = song.Year.HasValue ? song.Year.Value.ToString() : "Unknown";
        result = result.Replace("{year}", yearValue);

        // {genre} — genre, "Unknown" if null/empty
        var genreValue = string.IsNullOrWhiteSpace(song.Genre) ? "Unknown" : song.Genre;
        result = result.Replace("{genre}", genreValue);

        // {quality} — downloaded quality, "Unknown" if null/empty
        var qualityValue = string.IsNullOrWhiteSpace(downloadedQuality) ? "Unknown" : downloadedQuality;
        result = result.Replace("{quality}", qualityValue);

        // Clean up artifacts from empty placeholders:
        // If {track} was empty, we might have leftover " - " at the start of the segment
        // e.g., template "{track} - {title}" with no track → " - My Song" → "My Song"
        if (!song.Track.HasValue)
        {
            result = result.TrimStart(' ', '-').TrimStart();
        }

        return result;
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
