using System.Text.Json;
using System.Text.Json.Serialization;

namespace octo_fiesta.Models.SquidWTF;

#region Amazon Music API Responses (amz.squid.wtf)

/// <summary>
/// Response from POST /api/search with content_type TRACK or ALBUM
/// </summary>
public class AmazonMusicSearchResponse
{
    [JsonPropertyName("trackList")]
    public List<AmazonMusicSearchTrack>? TrackList { get; set; }

    [JsonPropertyName("albumList")]
    public List<AmazonMusicSearchAlbum>? AlbumList { get; set; }
}

public class AmazonMusicSearchTrack
{
    [JsonPropertyName("asin")]
    public string? Asin { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("primaryArtistName")]
    public string? PrimaryArtistName { get; set; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("albumArtistName")]
    public string? AlbumArtistName { get; set; }

    /// <summary>
    /// Album field can be a string (title only) or an object with title and image.
    /// Parsed by <see cref="AmazonMusicAlbumFieldConverter"/>.
    /// </summary>
    [JsonPropertyName("album")]
    [JsonConverter(typeof(AmazonMusicAlbumFieldConverter))]
    public AmazonMusicAlbumField? Album { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }
}

/// <summary>
/// Represents the "album" field in a search track result, which can be a plain string or an object.
/// </summary>
public class AmazonMusicAlbumField
{
    public string? Title { get; set; }
    public string? Image { get; set; }
}

public class AmazonMusicAlbumFieldConverter : JsonConverter<AmazonMusicAlbumField?>
{
    public override AmazonMusicAlbumField? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
            return new AmazonMusicAlbumField { Title = reader.GetString() };

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? title = null, image = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var propName = reader.GetString();
                reader.Read();
                switch (propName)
                {
                    case "title":
                        title = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        break;
                    case "image":
                        image = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            return new AmazonMusicAlbumField { Title = title, Image = image };
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, AmazonMusicAlbumField? value, JsonSerializerOptions options)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("title", value.Title);
        if (value.Image != null) writer.WriteString("image", value.Image);
        writer.WriteEndObject();
    }
}

public class AmazonMusicSearchAlbum
{
    [JsonPropertyName("asin")]
    public string? Asin { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("primaryArtistName")]
    public string? PrimaryArtistName { get; set; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("albumArtistName")]
    public string? AlbumArtistName { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }
}

/// <summary>
/// Response from POST /api/track — includes stream URL and full track metadata
/// </summary>
public class AmazonMusicTrackResponse
{
    [JsonPropertyName("metadata")]
    public AmazonMusicTrackMetadata? Metadata { get; set; }

    [JsonPropertyName("stream")]
    public AmazonMusicStream? Stream { get; set; }

    [JsonPropertyName("drm")]
    public AmazonMusicDrm? Drm { get; set; }

    [JsonPropertyName("atmosUnavailable")]
    public bool AtmosUnavailable { get; set; }
}

public class AmazonMusicDrm
{
    /// <summary>AES-128 key (32 hex chars) for CENC-decrypting the CMAF stream.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }
}

public class AmazonMusicTrackMetadata
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("album_artist")]
    public string? AlbumArtist { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("cover_cdn")]
    public string? CoverCdn { get; set; }

    [JsonPropertyName("album_asin")]
    public string? AlbumAsin { get; set; }

    /// <summary>Track number — may be a JSON number or string.</summary>
    [JsonPropertyName("track_number")]
    public JsonElement? TrackNumber { get; set; }

    /// <summary>Total tracks on the album — may be a JSON number or string.</summary>
    [JsonPropertyName("track_total")]
    public JsonElement? TrackTotal { get; set; }

    /// <summary>Disc number — may be a JSON number or string.</summary>
    [JsonPropertyName("disc_number")]
    public JsonElement? DiscNumber { get; set; }

    /// <summary>Total discs — may be a JSON number or string.</summary>
    [JsonPropertyName("disc_total")]
    public JsonElement? DiscTotal { get; set; }

    [JsonPropertyName("genre")]
    public string? Genre { get; set; }

    [JsonPropertyName("composer")]
    public string? Composer { get; set; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("isrc")]
    public string? Isrc { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public class AmazonMusicStream
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

}

/// <summary>
/// Response from POST /api/queue — ordered track list for an album URL
/// </summary>
public class AmazonMusicQueueResponse
{
    [JsonPropertyName("queue")]
    public List<AmazonMusicQueueItem>? Queue { get; set; }
}

public class AmazonMusicQueueItem
{
    [JsonPropertyName("asin")]
    public string? Asin { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("album_artist")]
    public string? AlbumArtist { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("track_number")]
    public JsonElement? TrackNumber { get; set; }

    [JsonPropertyName("disc_number")]
    public JsonElement? DiscNumber { get; set; }
}

#endregion
