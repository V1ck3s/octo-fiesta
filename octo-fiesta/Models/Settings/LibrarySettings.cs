namespace octo_fiesta.Models.Settings;

/// <summary>
/// Library storage settings.
/// </summary>
public class LibrarySettings
{
    /// <summary>
    /// Base path where downloaded tracks are stored.
    /// </summary>
    public string DownloadPath { get; set; } = "./downloads";

    /// <summary>
    /// When true (and StorageMode=Permanent), downloads are saved to
    /// {DownloadPath}/{username}/Artist/Album/Track.
    /// </summary>
    public bool UserSubfolders { get; set; } = false;
}
