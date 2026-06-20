using octo_fiesta.Models.Domain;

namespace octo_fiesta.Services.Lyrics;

/// <summary>
/// Looks up synchronized lyrics for a song from an external provider (LRCLIB).
/// </summary>
public interface ILyricsService
{
    /// <summary>Whether the lyrics feature is enabled.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Fetches lyrics for the given song. Returns null when nothing is found or the
    /// feature is disabled. Results (including misses) are cached in-memory.
    /// </summary>
    Task<SongLyrics?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a .lrc sidecar next to <paramref name="audioFilePath"/> when lyrics are
    /// available and Lyrics:WriteLrcFile is enabled. Best-effort: never throws.
    /// </summary>
    Task TryWriteSidecarAsync(string audioFilePath, Song song, CancellationToken cancellationToken = default);
}
