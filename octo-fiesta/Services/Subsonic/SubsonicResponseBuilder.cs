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
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
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
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
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
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
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
                    song = album.Songs.Select(s => ConvertSongToJson(s)).ToList()
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
                    new XAttribute("songCount", album.SongCount ?? 0),
                    new XAttribute("year", album.Year ?? 0),
                    new XAttribute("coverArt", album.Id),
                    album.Songs.Select(s => ConvertSongToXml(s, ns))
                )
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
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
                    created = playlist.CreatedDate?.ToString("yyyy-MM-ddTHH:mm:ss"),
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
            albumElement.Add(new XAttribute("created", playlist.CreatedDate.Value.ToString("yyyy-MM-ddTHH:mm:ss")));
        }
        
        // Add songs
        foreach (var song in tracks)
        {
            albumElement.Add(ConvertSongToXml(song, ns));
        }
        
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                albumElement
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
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
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
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
            ["coverArt"] = song.Id,
            ["suffix"] = song.IsLocal ? "mp3" : "Remote",
            ["contentType"] = "audio/mpeg",
            ["type"] = "music",
            ["isVideo"] = false,
            ["isExternal"] = !song.IsLocal
        };
        
        result["bitRate"] = song.IsLocal ? 128 : 0; // Default bitrate for local files
        
        return result;
    }

    /// <summary>
    /// Converts an Album domain model to Subsonic JSON format.
    /// </summary>
    public object ConvertAlbumToJson(Album album)
    {
        return new
        {
            id = album.Id,
            name = album.Title,
            artist = album.Artist,
            artistId = album.ArtistId,
            songCount = album.SongCount ?? 0,
            year = album.Year ?? 0,
            coverArt = album.Id,
            isExternal = !album.IsLocal
        };
    }

    /// <summary>
    /// Converts an Artist domain model to Subsonic JSON format.
    /// </summary>
    public object ConvertArtistToJson(Artist artist)
    {
        return new
        {
            id = artist.Id,
            name = artist.Name,
            albumCount = artist.AlbumCount ?? 0,
            coverArt = artist.Id,
            isExternal = !artist.IsLocal
        };
    }

    /// <summary>
    /// Converts a Song domain model to Subsonic XML format.
    /// </summary>
    public XElement ConvertSongToXml(Song song, XNamespace ns)
    {
        return new XElement(ns + "song",
            new XAttribute("id", song.Id),
            new XAttribute("title", song.Title),
            new XAttribute("album", song.Album ?? ""),
            new XAttribute("artist", song.Artist ?? ""),
            new XAttribute("duration", song.Duration ?? 0),
            new XAttribute("track", song.Track ?? 0),
            new XAttribute("year", song.Year ?? 0),
            new XAttribute("coverArt", song.Id),
            new XAttribute("isExternal", (!song.IsLocal).ToString().ToLower())
        );
    }

    /// <summary>
    /// Converts an Album domain model to Subsonic XML format.
    /// </summary>
    public XElement ConvertAlbumToXml(Album album, XNamespace ns)
    {
        return new XElement(ns + "album",
            new XAttribute("id", album.Id),
            new XAttribute("name", album.Title),
            new XAttribute("artist", album.Artist ?? ""),
            new XAttribute("songCount", album.SongCount ?? 0),
            new XAttribute("year", album.Year ?? 0),
            new XAttribute("coverArt", album.Id),
            new XAttribute("isExternal", (!album.IsLocal).ToString().ToLower())
        );
    }

    /// <summary>
    /// Converts an Artist domain model to Subsonic XML format.
    /// </summary>
    public XElement ConvertArtistToXml(Artist artist, XNamespace ns)
    {
        return new XElement(ns + "artist",
            new XAttribute("id", artist.Id),
            new XAttribute("name", artist.Name),
            new XAttribute("albumCount", artist.AlbumCount ?? 0),
            new XAttribute("coverArt", artist.Id),
            new XAttribute("isExternal", (!artist.IsLocal).ToString().ToLower())
        );
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
