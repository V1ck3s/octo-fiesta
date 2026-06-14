using Microsoft.AspNetCore.Mvc;
using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Subsonic;
using System.Text.Json;
using System.Xml.Linq;

namespace octo_fiesta.Tests;

public class SubsonicResponseBuilderLyricsTests
{
    private readonly SubsonicResponseBuilder _builder = new();

    [Fact]
    public void CreateLyricsBySongIdResponse_Json_Synced_BuildsStructuredLyrics()
    {
        var lyrics = new SongLyrics
        {
            DisplayArtist = "Daft Punk",
            DisplayTitle = "Get Lucky",
            Synced = true,
            Lines = { new LyricLine(0, "Like the legend of the phoenix") }
        };

        var result = _builder.CreateLyricsBySongIdResponse("json", lyrics);

        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value);
        var root = JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");
        Assert.Equal("ok", root.GetProperty("status").GetString());

        var structured = root.GetProperty("lyricsList").GetProperty("structuredLyrics")[0];
        Assert.Equal("Daft Punk", structured.GetProperty("displayArtist").GetString());
        Assert.True(structured.GetProperty("synced").GetBoolean());

        var line = structured.GetProperty("line")[0];
        Assert.Equal(0, line.GetProperty("start").GetInt64());
        Assert.Equal("Like the legend of the phoenix", line.GetProperty("value").GetString());
    }

    [Fact]
    public void CreateLyricsBySongIdResponse_Json_Unsynced_OmitsStart()
    {
        var lyrics = new SongLyrics
        {
            DisplayArtist = "A",
            DisplayTitle = "B",
            Synced = false,
            Lines = { new LyricLine(0, "plain line") }
        };

        var result = _builder.CreateLyricsBySongIdResponse("json", lyrics);

        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value);
        var structured = JsonDocument.Parse(json).RootElement
            .GetProperty("subsonic-response").GetProperty("lyricsList")
            .GetProperty("structuredLyrics")[0];

        Assert.False(structured.GetProperty("synced").GetBoolean());
        var line = structured.GetProperty("line")[0];
        Assert.False(line.TryGetProperty("start", out _));
        Assert.Equal("plain line", line.GetProperty("value").GetString());
    }

    [Fact]
    public void CreateLyricsBySongIdResponse_Json_Null_ReturnsEmptyLyricsList()
    {
        var result = _builder.CreateLyricsBySongIdResponse("json", null);

        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value);
        var root = JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("lyricsList").TryGetProperty("structuredLyrics", out _));
    }

    [Fact]
    public void CreateLyricsBySongIdResponse_Xml_Synced_BuildsLineElementsWithStart()
    {
        var lyrics = new SongLyrics
        {
            DisplayArtist = "Daft Punk",
            DisplayTitle = "Get Lucky",
            Synced = true,
            Lines =
            {
                new LyricLine(1000, "one"),
                new LyricLine(2000, "two")
            }
        };

        var result = _builder.CreateLyricsBySongIdResponse("xml", lyrics);

        var content = Assert.IsType<ContentResult>(result);
        var doc = XDocument.Parse(content.Content!);
        XNamespace ns = "http://subsonic.org/restapi";

        var structured = doc.Root!.Element(ns + "lyricsList")!.Element(ns + "structuredLyrics")!;
        Assert.Equal("true", structured.Attribute("synced")?.Value);

        var lines = structured.Elements(ns + "line").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("1000", lines[0].Attribute("start")?.Value);
        Assert.Equal("one", lines[0].Value);
    }
}
