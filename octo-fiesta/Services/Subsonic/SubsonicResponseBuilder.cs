using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;
using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.Subsonic;

/// <summary>
/// Handles building Subsonic API responses in both XML and JSON formats.
/// </summary>
public class SubsonicResponseBuilder
{
    private const string SubsonicNamespace = "http://subsonic.org/restapi";
    private const string SubsonicVersion = "1.16.1";

    /// <summary>
    /// Creates a generic Subsonic response with status "ok".
    /// </summary>
    public IActionResult CreateResponse(string format, string elementName, object data)
    {
        if (format == "json")
        {
            return CreateJsonResponse(new { status = "ok", version = SubsonicVersion });
        }
        
        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                new XElement(ns + elementName)
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
    }

    /// <summary>
    /// Creates a Subsonic error response.
    /// </summary>
    public IActionResult CreateError(string format, int code, string message)
    {
        if (format == "json")
        {
            return CreateJsonResponse(new 
            { 
                status = "failed", 
                version = SubsonicVersion,
                error = new { code, message }
            });
        }
        
        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "failed"),
                new XAttribute("version", SubsonicVersion),
                new XElement(ns + "error",
                    new XAttribute("code", code),
                    new XAttribute("message", message)
                )
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
    }

    /// <summary>
    /// Creates a Subsonic response containing a single song.
    /// </summary>
    public IActionResult CreateSongResponse(string format, Song song)
    {
        if (format == "json")
        {
            return CreateJsonResponse(new 
            { 
                status = "ok", 
                version = SubsonicVersion,
                song = ConvertSongToJson(song)
            });
        }
        
        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                ConvertSongToXml(song, ns)
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
    }

    /// <summary>
    /// Creates a Subsonic response containing an album with songs.
    /// </summary>
    public IActionResult CreateAlbumResponse(string format, Album album)
    {
        var totalDuration = album.Songs.Sum(s => s.Duration ?? 0);
        
        if (format == "json")
        {
            return CreateJsonResponse(new 
            { 
                status = "ok", 
                version = SubsonicVersion,
                album = new
                {
                    id = album.Id,
                    name = album.Title,
                    artist = album.Artist,
                    artistId = album.ArtistId,
                    coverArt = album.Id,
                    songCount = album.Songs.Count > 0 ? album.Songs.Count : (album.SongCount ?? 0),
                    duration = totalDuration,
                    year = album.Year ?? 0,
                    genre = album.Genre ?? "",
                    isCompilation = false,
                    song = (album.Songs ?? Enumerable.Empty<Song>()).Select(s => ConvertSongToJson(s)).ToList()
                }
            });
        }
        
        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                new XElement(ns + "album",
                    new XAttribute("id", album.Id),
                    new XAttribute("name", album.Title),
                    new XAttribute("artist", album.Artist ?? ""),
                    new XAttribute("artistId", album.ArtistId ?? string.Empty),
                    new XAttribute("songCount", album.Songs?.Count ?? album.SongCount ?? 0),
                    new XAttribute("duration", totalDuration),
                    new XAttribute("year", album.Year ?? 0),
                    new XAttribute("coverArt", album.Id),
                    (album.Songs?.Select(s => ConvertSongToXml(s, ns, album.Id)) ?? Enumerable.Empty<XElement>())
                )
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
    }
    
    /// <summary>
    /// Creates a Subsonic response for a playlist represented as an album.
    /// Playlists appear as albums with genre "Playlist".
    /// </summary>
    public IActionResult CreatePlaylistAsAlbumResponse(string format, ExternalPlaylist playlist, List<Song> tracks)
    {
        var totalDuration = tracks.Sum(s => s.Duration ?? 0);
        
        // Build artist name with emoji and curator
        var artistName = $"🎵 {char.ToUpper(playlist.Provider[0])}{playlist.Provider.Substring(1)}";
        if (!string.IsNullOrEmpty(playlist.CuratorName))
        {
            artistName += $" {playlist.CuratorName}";
        }
        
        var artistId = $"curator-{playlist.Provider}-{playlist.CuratorName?.ToLowerInvariant().Replace(" ", "-") ?? "unknown"}";
        
        if (format == "json")
        {
            return CreateJsonResponse(new 
            { 
                status = "ok", 
                version = SubsonicVersion,
                album = new
                {
                    id = playlist.Id,
                    name = playlist.Name,
                    artist = artistName,
                    artistId = artistId,
                    coverArt = playlist.Id,
                    songCount = tracks.Count,
                    duration = totalDuration,
                    year = playlist.CreatedDate?.Year ?? 0,
                    genre = "Playlist",
                    isCompilation = false,
                    created = playlist.CreatedDate.HasValue ? playlist.CreatedDate.Value.ToUniversalTime().ToString("o") : null,
                    song = tracks.Select(s => ConvertSongToJson(s)).ToList()
                }
            });
        }
        
        var ns = XNamespace.Get(SubsonicNamespace);
        var albumElement = new XElement(ns + "album",
            new XAttribute("id", playlist.Id),
            new XAttribute("name", playlist.Name),
            new XAttribute("artist", artistName),
            new XAttribute("artistId", artistId),
            new XAttribute("songCount", tracks.Count),
            new XAttribute("duration", totalDuration),
            new XAttribute("genre", "Playlist"),
            new XAttribute("coverArt", playlist.Id)
        );
        
        if (playlist.CreatedDate.HasValue)
        {
            albumElement.Add(new XAttribute("year", playlist.CreatedDate.Value.Year));
            albumElement.Add(new XAttribute("created", playlist.CreatedDate.Value.ToUniversalTime().ToString("o")));
        }
        
        // Add songs
        foreach (var song in tracks)
        {
            albumElement.Add(ConvertSongToXml(song, ns, playlist.Id));
        }
        
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                albumElement
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
    }

    /// <summary>
    /// Creates a Subsonic response containing an artist with albums.
    /// </summary>
    public IActionResult CreateArtistResponse(string format, Artist artist, List<Album> albums)
    {
        if (format == "json")
        {
            return CreateJsonResponse(new 
            { 
                status = "ok", 
                version = SubsonicVersion,
                artist = new
                {
                    id = artist.Id,
                    name = artist.Name,
                    coverArt = artist.Id,
                    albumCount = albums.Count,
                    artistImageUrl = artist.ImageUrl,
                    album = albums.Select(a => ConvertAlbumToJson(a)).ToList()
                }
            });
        }
        
        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                new XElement(ns + "artist",
                    new XAttribute("id", artist.Id),
                    new XAttribute("name", artist.Name),
                    new XAttribute("coverArt", artist.Id),
                    new XAttribute("albumCount", albums.Count),
                    albums.Select(a => ConvertAlbumToXml(a, ns))
                )
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
    }

    /// <summary>
    /// Creates a JSON Subsonic response with "subsonic-response" key (with hyphen).
    /// </summary>
    public IActionResult CreateJsonResponse(object responseContent)
    {
        var response = new Dictionary<string, object>
        {
            ["subsonic-response"] = responseContent
        };
        return new JsonResult(response);
    }

    /// <summary>
    /// Converts a Song domain model to Subsonic JSON format.
    /// </summary>
    public Dictionary<string, object> ConvertSongToJson(Song song)
    {
        var (suffix, contentType, bitRate) = GetSuffixContentTypeAndBitrate(song);

        long size = 0;
        string? created = null;
        try
        {
            if (!string.IsNullOrEmpty(song.LocalPath) && System.IO.File.Exists(song.LocalPath))
            {
                var fi = new System.IO.FileInfo(song.LocalPath);
                size = fi.Length;
                created = fi.LastWriteTimeUtc.ToString("o");
            }
            else if (!string.IsNullOrEmpty(song.ReleaseDate))
            {
                if (System.DateTime.TryParse(song.ReleaseDate, out var dt))
                {
                    created = dt.ToUniversalTime().ToString("o");
                }
            }
        }
        catch (System.IO.IOException)
        {
            // best effort: ignore file I/O errors when determining size/created
        }
        catch (System.UnauthorizedAccessException)
        {
            // best effort: ignore permission issues when determining size/created
        }

        if (size == 0 && (song.Duration ?? 0) > 0 && bitRate > 0)
        {
            // size (bytes) = bitRate (kbps) * 125 (bytes/sec per kbps) * duration (sec)
            size = (long)bitRate * 125L * (long)(song.Duration ?? 0);
        }

        var result = new Dictionary<string, object>
        {
            ["id"] = song.Id,
            ["parent"] = song.AlbumId ?? "",
            ["isDir"] = false,
            ["title"] = song.Title,
            ["album"] = song.Album ?? "",
            ["artist"] = song.Artist ?? "",
            ["albumId"] = song.AlbumId ?? "",
            ["artistId"] = song.ArtistId ?? "",
            ["duration"] = song.Duration ?? 0,
            ["track"] = song.Track ?? 0,
            ["year"] = song.Year ?? 0,
            ["suffix"] = suffix,
            ["contentType"] = contentType,
            ["bitRate"] = bitRate,
            ["size"] = size,
            ["type"] = "music",
            ["isVideo"] = false,
            ["isExternal"] = !song.IsLocal
        };

        // Only include coverArt if the song has a cover URL (avoids broken images for songs without covers)
        if (song.IsLocal || !string.IsNullOrEmpty(song.CoverArtUrl))
        {
            result["coverArt"] = song.Id;
        }

        if (created != null)
        {
            result["created"] = created;
        }

        return result;
    }

    /// <summary>
    /// Converts an Album domain model to Subsonic JSON format.
    /// </summary>
    public object ConvertAlbumToJson(Album album)
    {
        var result = new Dictionary<string, object>
        {
            ["id"] = album.Id,
            ["name"] = album.Title,
            ["artist"] = album.Artist ?? "",
            ["artistId"] = album.ArtistId ?? "",
            ["songCount"] = album.SongCount ?? 0,
            ["year"] = album.Year ?? 0,
            ["isExternal"] = !album.IsLocal
        };

        // Only include coverArt if the album has a cover URL (avoids broken images)
        if (album.IsLocal || !string.IsNullOrEmpty(album.CoverArtUrl))
        {
            result["coverArt"] = album.Id;
        }

        return result;
    }

    /// <summary>
    /// Converts an Artist domain model to Subsonic JSON format.
    /// </summary>
    public object ConvertArtistToJson(Artist artist)
    {
        var result = new Dictionary<string, object>
        {
            ["id"] = artist.Id,
            ["name"] = artist.Name,
            ["albumCount"] = artist.AlbumCount ?? 0,
            ["isExternal"] = !artist.IsLocal
        };

        // Only include coverArt if the artist has an image URL (avoids broken images)
        if (artist.IsLocal || !string.IsNullOrEmpty(artist.ImageUrl))
        {
            result["coverArt"] = artist.Id;
        }

        return result;
    }

    /// <summary>
    /// Converts a Song domain model to Subsonic XML format.
    /// Includes attributes Amperfy expects: albumId/parent, artistId, size, created, suffix, contentType, bitRate.
    /// </summary>
    public XElement ConvertSongToXml(Song song, XNamespace ns, string? parentAlbumId = null)
    {
        var isSquid = !string.IsNullOrEmpty(song.ExternalProvider) && song.ExternalProvider.Equals("SquidWTF", System.StringComparison.OrdinalIgnoreCase);

        // albumId/parent prefer explicit Song.AlbumId, otherwise fall back to provided parentAlbumId
        var albumId = song.AlbumId ?? parentAlbumId ?? string.Empty;

        long size = 0;
        string? created = null;
        try
        {
            // If we have a local path, try to get file size & last write time
            if (!string.IsNullOrEmpty(song.LocalPath) && System.IO.File.Exists(song.LocalPath))
            {
                var fi = new System.IO.FileInfo(song.LocalPath);
                size = fi.Length;
                created = fi.LastWriteTimeUtc.ToString("o");
            }
            else if (!string.IsNullOrEmpty(song.ReleaseDate))
            {
                if (System.DateTime.TryParse(song.ReleaseDate, out var dt))
                {
                    created = dt.ToUniversalTime().ToString("o");
                }
            }
        }
        catch {
            // Best-effort: ignore filesystem errors
        }

        // Determine suffix, contentType and bit rate (kbps), and estimate size if missing
        var (suffix, contentType, bitRate) = GetSuffixContentTypeAndBitrate(song);
        if (size == 0 && (song.Duration ?? 0) > 0 && bitRate > 0)
        {
            // size (bytes) = bitRate (kbps) * 125 (bytes/sec per kbps) * duration (sec)
            var duration = (long)(song.Duration ?? 0);
            size = (long)bitRate * 125L * duration;
        }

        var songElement = new XElement(ns + "song",
            new XAttribute("id", song.Id),
            new XAttribute("title", song.Title),
            new XAttribute("album", song.Album ?? ""),
            new XAttribute("albumId", albumId),
            new XAttribute("parent", albumId),
            new XAttribute("artist", song.Artist ?? ""),
            new XAttribute("duration", song.Duration ?? 0),
            new XAttribute("track", song.Track ?? 0),
            new XAttribute("year", song.Year ?? 0),
            new XAttribute("suffix", suffix),
            new XAttribute("contentType", contentType),
            new XAttribute("type", "music"),
            new XAttribute("isVideo", "false"),
            new XAttribute("bitRate", bitRate),
            new XAttribute("size", size),
            new XAttribute("isDir", "false"),
            new XAttribute("isExternal", (!song.IsLocal).ToString().ToLower())
        );

        // Only include coverArt if the song has a cover URL (avoids broken images for songs without covers)
        if (song.IsLocal || !string.IsNullOrEmpty(song.CoverArtUrl))
        {
            songElement.Add(new XAttribute("coverArt", song.Id));
        }

        if (!string.IsNullOrEmpty(song.ArtistId))
        {
            songElement.Add(new XAttribute("artistId", song.ArtistId));
        }

        if (!string.IsNullOrEmpty(created))
        {
            songElement.Add(new XAttribute("created", created));
        }

        return songElement;
    }

    /// <summary>
    /// Converts an Album domain model to Subsonic XML format.
    /// Includes songCount (based on actual track list when available) and total duration.
    /// </summary>
    public XElement ConvertAlbumToXml(Album album, XNamespace ns)
    {
        var totalDuration = album.Songs?.Sum(s => s.Duration ?? 0) ?? 0;
        var element = new XElement(ns + "album",
            new XAttribute("id", album.Id),
            new XAttribute("name", album.Title),
            new XAttribute("artist", album.Artist ?? ""),
            new XAttribute("artistId", album.ArtistId ?? string.Empty),
            new XAttribute("songCount", album.Songs?.Count ?? album.SongCount ?? 0),
            new XAttribute("duration", totalDuration),
            new XAttribute("year", album.Year ?? 0),
            new XAttribute("isExternal", (!album.IsLocal).ToString().ToLower())
        );

        // Only include coverArt if the album has a cover URL (avoids broken images)
        if (album.IsLocal || !string.IsNullOrEmpty(album.CoverArtUrl))
        {
            element.Add(new XAttribute("coverArt", album.Id));
        }

        if (!string.IsNullOrEmpty(album.Genre))
        {
            element.Add(new XAttribute("genre", album.Genre));
        }

        return element;
    }

    /// <summary>
    /// Converts an Artist domain model to Subsonic XML format.
    /// </summary>
    public XElement ConvertArtistToXml(Artist artist, XNamespace ns)
    {
        var element = new XElement(ns + "artist",
            new XAttribute("id", artist.Id),
            new XAttribute("name", artist.Name),
            new XAttribute("albumCount", artist.AlbumCount ?? 0),
            new XAttribute("isExternal", (!artist.IsLocal).ToString().ToLower())
        );

        // Only include coverArt if the artist has an image URL (avoids broken images)
        if (artist.IsLocal || !string.IsNullOrEmpty(artist.ImageUrl))
        {
            element.Add(new XAttribute("coverArt", artist.Id));
        }

        return element;
    }

    /// <summary>
    /// Converts a Subsonic JSON element to a dictionary.
    /// </summary>
    public object ConvertSubsonicJsonElement(JsonElement element, bool isLocal)
    {
        var dict = new Dictionary<string, object>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonValue(prop.Value);
        }
        dict["isExternal"] = !isLocal;
        return dict;
    }

    /// <summary>
    /// Converts a Subsonic XML element.
    /// </summary>
    public XElement ConvertSubsonicXmlElement(XElement element, string type)
    {
        var newElement = new XElement(element);
        newElement.SetAttributeValue("isExternal", "false");
        return newElement;
    }

    /// <summary>
    /// Determines the file suffix, MIME content type, and bitrate based on the song's provider and local path.
    /// Supports FLAC, M4A (AAC), and MP3 formats for local files, and provider-specific formats for external files.
    /// </summary>
    private static (string suffix, string contentType, int bitRate) GetSuffixContentTypeAndBitrate(Song song)
    {
        // For cached/downloaded files, determine format from file extension
        if (!string.IsNullOrEmpty(song.LocalPath))
        {
            var extension = Path.GetExtension(song.LocalPath).ToLowerInvariant();
            return extension switch
            {
                ".flac" => ("flac", "audio/flac", 1411),
                ".m4a" => ("m4a", "audio/mp4", 320),
                ".mp3" => ("mp3", "audio/mpeg", 320),
                _ => ("mp3", "audio/mpeg", 128)
            };
        }

        // For local library files without path info
        if (song.IsLocal)
        {
            return ("mp3", "audio/mpeg", 128);
        }

        // Default for external providers (Deezer, Qobuz, SquidWTF) without cached file
        return ("Remote", "audio/mpeg", 0);
    }

    private object ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToList(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonValue(p.Value)),
            JsonValueKind.Null => null!,
            _ => value.ToString()
        };
    }
}
