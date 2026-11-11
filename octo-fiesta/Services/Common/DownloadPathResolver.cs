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
    private const string MappingsKey = "MappingsPath";
    private const string MappingsFileName = ".mappings.json";
    private const string DefaultPath = "./downloads";

    public static string GetSharedPath(IConfiguration configuration)
    {
        var shared = configuration[$"{SharedSection}:{PathKey}"];
        return string.IsNullOrWhiteSpace(shared) ? DefaultPath : shared;
    }

    /// <summary>
    /// Path of the LocalLibraryService mappings file. Defaults to ".mappings.json"
    /// inside the shared download path; can be pointed at a local disk when the
    /// download directory lives on a remote/virtual filesystem where small
    /// writes are unreliable. Per-provider download paths do not affect this.
    /// An empty override counts as absent.
    /// </summary>
    public static string GetMappingsPath(IConfiguration configuration)
    {
        var mappings = configuration[$"{SharedSection}:{MappingsKey}"];
        return string.IsNullOrWhiteSpace(mappings)
            ? Path.Combine(GetSharedPath(configuration), MappingsFileName)
            : mappings;
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
}
