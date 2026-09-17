using System.Text;
using System.Xml.Linq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.Subsonic;

public partial class SubsonicModelMapper
{
    /// <summary>
    /// Fork overload: filters the external results with the fork's dedupe rules, then
    /// delegates to the upstream merge so upstream changes to it keep applying.
    /// </summary>
    public (List<object> MergedSongs, List<object> MergedAlbums, List<object> MergedArtists) MergeSearchResults(
        List<object> localSongs,
        List<object> localAlbums,
        List<object> localArtists,
        SearchResult externalResult,
        List<ExternalPlaylist> externalPlaylists,
        IReadOnlyDictionary<string, LocalSongMapping>? mappings,
        bool isJson)
    {
        var localSongIds = new HashSet<string>(StringComparer.Ordinal);
        var localSongKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var song in localSongs)
        {
            if (LocalField(song, "id") is { Length: > 0 } id)
            {
                localSongIds.Add(id);
            }
            var key = BuildSongKey(LocalField(song, "artist"), LocalField(song, "title"));
            if (key != null)
            {
                localSongKeys.Add(key);
            }
        }

        var localAlbumKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var album in localAlbums)
        {
            // Subsonic uses "name" for album titles; some clients expose "title" too.
            var key = BuildAlbumKey(LocalField(album, "artist"), LocalField(album, "name") ?? LocalField(album, "title"));
            if (key != null)
            {
                localAlbumKeys.Add(key);
            }
        }

        var localArtistKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artist in localArtists)
        {
            var name = LocalField(artist, "name");
            if (!string.IsNullOrEmpty(name))
            {
                localArtistKeys.Add(StringNormalizer.CreateArtistComparisonKey(name));
            }
        }

        // Artists are left out here: the fork matches them case-sensitively, which keeps
        // artists upstream's case-insensitive check would drop, so they are appended below.
        var filtered = new SearchResult
        {
            Songs = externalResult.Songs
                .Where(s => !ShouldDropExternalSong(s, mappings, localSongIds, localSongKeys))
                .ToList(),
            Albums = externalResult.Albums
                .Where(a => !ShouldDropExternalAlbum(a, localAlbumKeys))
                .ToList(),
        };

        // Providers (notably Qobuz) sometimes return several near-duplicate yearly snapshots
        // of the same curated list; collapse those before they are emitted as albums.
        var merged = MergeSearchResults(
            localSongs,
            localAlbums,
            localArtists,
            filtered,
            DeduplicateExternalPlaylists(externalPlaylists).ToList(),
            isJson);

        var ns = XNamespace.Get("http://subsonic.org/restapi");
        foreach (var artist in externalResult.Artists)
        {
            if (!localArtistKeys.Contains(StringNormalizer.CreateArtistComparisonKey(artist.Name)))
            {
                merged.MergedArtists.Add(isJson
                    ? _responseBuilder.ConvertArtistToJson(artist)
                    : _responseBuilder.ConvertArtistToXml(artist, ns));
            }
        }

        return merged;
    }

    /// <summary>
    /// Reads a field from a local search3 entry: a JSON dictionary or an XML element.
    /// </summary>
    private static string? LocalField(object entry, string name) => entry switch
    {
        Dictionary<string, object> dict => dict.TryGetValue(name, out var value) ? value?.ToString() : null,
        XElement element => element.Attribute(name)?.Value,
        _ => null,
    };

    /// <summary>
    /// Decides whether an external <paramref name="song"/> already has a local
    /// equivalent in the search response and should be hidden.
    /// </summary>
    private static bool ShouldDropExternalSong(
        Song song,
        IReadOnlyDictionary<string, LocalSongMapping>? mappings,
        HashSet<string> localSongIds,
        HashSet<string> localSongKeys)
    {
        // Tier 1: precise mapping check. The user has actually downloaded this exact
        // ext id via Octo Fiesta and Navidrome returned the resulting local song in
        // this same response — drop the duplicate.
        if (mappings != null
            && !string.IsNullOrEmpty(song.ExternalProvider)
            && !string.IsNullOrEmpty(song.ExternalId)
            && mappings.TryGetValue($"{song.ExternalProvider}:{song.ExternalId}", out var mapping)
            && !string.IsNullOrEmpty(mapping.LocalSubsonicId)
            && localSongIds.Contains(mapping.LocalSubsonicId))
        {
            return true;
        }

        // Tier 2: metadata fallback. Catches pre-existing library songs (no mapping row)
        // that Navidrome already surfaced for the same query.
        var key = BuildSongKey(song.Artist, song.Title);
        return key != null && localSongKeys.Contains(key);
    }

    /// <summary>
    /// Drops external albums whose normalized (artist, title) matches an album returned
    /// by the local Navidrome search. There is no album-level mapping store, so this
    /// relies on metadata alone.
    /// </summary>
    private static bool ShouldDropExternalAlbum(Album album, HashSet<string> localAlbumKeys)
    {
        var key = BuildAlbumKey(album.Artist, album.Title);
        return key != null && localAlbumKeys.Contains(key);
    }

    private static string? BuildSongKey(string? artist, string? title)
    {
        var artistKey = StringNormalizer.CreateComparisonKey(artist);
        var titleKey = StringNormalizer.CreateSongTitleDedupeKey(title);
        if (artistKey.Length == 0 || titleKey.Length == 0)
        {
            return null;
        }
        return artistKey + "\u0001" + titleKey;
    }

    private static string? BuildAlbumKey(string? artist, string? title)
    {
        var artistKey = StringNormalizer.CreateComparisonKey(artist);
        var titleKey = StringNormalizer.CreateComparisonKey(title);
        if (artistKey.Length == 0 || titleKey.Length == 0)
        {
            return null;
        }
        return artistKey + "\u0001" + titleKey;
    }

    /// <summary>
    /// Collapses external playlists that share the same normalized
    /// (provider, name, curator) tuple. Preserves insertion order and keeps the
    /// first occurrence. Entries with an empty/whitespace <see cref="ExternalPlaylist.Name"/>
    /// are passed through untouched so we never silently merge unidentified rows.
    /// </summary>
    private static IEnumerable<ExternalPlaylist> DeduplicateExternalPlaylists(
        IEnumerable<ExternalPlaylist> playlists)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var playlist in playlists)
        {
            if (string.IsNullOrWhiteSpace(playlist.Name))
            {
                yield return playlist;
                continue;
            }

            var providerKey = NormalizePlaylistKeyPart(playlist.Provider);
            var nameKey = NormalizePlaylistKeyPart(playlist.Name);
            var curatorKey = NormalizePlaylistKeyPart(playlist.CuratorName ?? string.Empty);
            var key = providerKey + "\u0001" + nameKey + "\u0001" + curatorKey;

            if (seen.Add(key))
            {
                yield return playlist;
            }
        }
    }

    /// <summary>
    /// Normalizes a single component of the playlist dedupe key. Builds on top of
    /// <see cref="StringNormalizer.CreateComparisonKey"/> but additionally trims and
    /// collapses runs of internal whitespace so trivial padding differences in the
    /// provider's response don't keep otherwise identical playlists separate.
    /// </summary>
    private static string NormalizePlaylistKeyPart(string? input)
    {
        var normalized = StringNormalizer.CreateComparisonKey(input);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(normalized.Length);
        var prevSpace = true;
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }

        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
        {
            sb.Length -= 1;
        }
        return sb.ToString();
    }
    
}
