namespace octo_fiesta.Services.Common;

/// <summary>
/// Resolves the directories downloads land in. Every provider writes under
/// "Library:DownloadPath" unless it declares its own "{ProviderName}:DownloadPath", e.g.
/// Deezer__DownloadPath. IConfiguration keys are case-insensitive, so a provider's lowercase
/// ProviderName matches its PascalCase section and needs no registration here.
/// </summary>
public static class DownloadPathResolver
{
    private const string SharedSection = "Library";
    private const string PathKey = "DownloadPath";
    private const string DefaultPath = "./downloads";

    public static string GetSharedPath(IConfiguration configuration)
    {
        var shared = configuration[$"{SharedSection}:{PathKey}"];
        return string.IsNullOrWhiteSpace(shared) ? DefaultPath : shared;
    }

    /// <summary>
    /// An override set to an empty value counts as absent, so a compose variable left blank
    /// does not silently redirect downloads to the working directory.
    /// </summary>
    public static string GetProviderPath(IConfiguration configuration, string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return GetSharedPath(configuration);
        }

        var providerPath = configuration[$"{providerName}:{PathKey}"];
        return string.IsNullOrWhiteSpace(providerPath) ? GetSharedPath(configuration) : providerPath;
    }

    /// <summary>
    /// Every distinct directory a downloaded file can live in, shared path first. Any section
    /// carrying a DownloadPath counts, so a provider added later needs no change here.
    /// </summary>
    public static IReadOnlyList<string> GetAllRoots(IConfiguration configuration)
    {
        var roots = new List<string> { GetSharedPath(configuration) };

        foreach (var section in configuration.GetChildren())
        {
            if (string.Equals(section.Key, SharedSection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = section[PathKey];
            if (string.IsNullOrWhiteSpace(path) || roots.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            roots.Add(path);
        }

        return roots;
    }

    public static void EnsureRootsExist(IConfiguration configuration)
    {
        foreach (var root in GetAllRoots(configuration))
        {
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }
        }
    }

    /// <summary>
    /// Relative while the track stays under the library the playlist lives in, absolute once a
    /// provider override puts it elsewhere, because a relative path climbing out of the library
    /// root does not resolve on the music server.
    /// </summary>
    public static string BuildPlaylistEntryPath(string playlistDirectory, string libraryRoot, string trackPath)
    {
        var entry = IsUnder(libraryRoot, trackPath)
            ? Path.GetRelativePath(playlistDirectory, trackPath)
            : Path.GetFullPath(trackPath);

        return entry.Replace('\\', '/');
    }

    private static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));

        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar);
    }
}
