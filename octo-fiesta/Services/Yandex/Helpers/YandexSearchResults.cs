using octo_fiesta.Models.Yandex;

namespace octo_fiesta.Services.Yandex;

/// <summary>
/// Enum-like class with strings for each value.
/// Used to define what to search with Yandex Music API.
/// </summary>
public sealed class YandexSearchItemsType
{
    public readonly string Value;
    private YandexSearchItemsType(string value) => Value = value;

    public static readonly YandexSearchItemsType TRACK = new("track");
    public static readonly YandexSearchItemsType ALBUM = new("album");
    public static readonly YandexSearchItemsType ARTIST = new("artist");
    public static readonly YandexSearchItemsType PLAYLIST = new("playlist");
    public static readonly YandexSearchItemsType ALL = new("all");
}

/// <summary>
/// Search limits for different types of items.
/// Zeros by default means if no value for specific type passed
/// then that item won't be collected from search results.
/// </summary>
/// <param name="track">Amount of Tracks to search.</param>
/// <param name="album">Amount of Albums to search.</param>
/// <param name="artist">Amount of Artists to search.</param>
/// <param name="playlist">Amount of Playlists to search.</param>
public class YandexSearchLimits(
    int track = 0,
    int album = 0,
    int artist = 0,
    int playlist = 0
)
{
    public readonly int Track = track;
    public readonly int Album = album;
    public readonly int Artist = artist;
    public readonly int Playlist = playlist;
}

/// <summary>
/// Utility wrapper for list of Tracks/Albums/Artists/Playlists
/// obtained form Yandex Music search API.
/// </summary>
/// <typeparam name="T">Type of contained elements.</typeparam>
/// <param name="limit">Maximum amount of elements to hold.</param>
public class CollectedSearchResults<T>(int limit)
{
    private readonly int Limit = limit;
    public List<T> Items = new(limit);
    public bool LimitReached { get => Items.Count >= Limit; }
    
    public void AddResultsIfNeeded(List<T>? results)
    {
        if (results is not null) {
            Items.AddRange(results.Take(Limit - Items.Count));
        }
    }
}

/// <summary>
/// Combined search results of all types.
/// </summary>
public class CombinedSearchResults(YandexSearchLimits limits)
{
    public CollectedSearchResults<YandexTrack> Tracks = new(limits.Track);
    public CollectedSearchResults<YandexAlbumId> Albums = new(limits.Album);
    public CollectedSearchResults<YandexArtist> Artists = new(limits.Artist);
    public CollectedSearchResults<YandexPlaylist> Playlists = new(limits.Playlist);
}
