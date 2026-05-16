using System.Globalization;
using System.Xml.Linq;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Parses a Tidal DASH MPD manifest (XML) into a flat list of segment URLs.
/// HI_RES_LOSSLESS tracks are now served as fragmented MP4 with FLAC inside, expressed
/// as a DASH manifest. The init segment plus all media segments form a single fMP4 file.
/// </summary>
public static class TidalDashManifestParser
{
    public sealed record ParsedDashManifest(string? Codecs, string? MimeType, IReadOnlyList<string> Urls);

    public static ParsedDashManifest Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root
            ?? throw new InvalidOperationException("DASH manifest is empty");
        var ns = root.GetDefaultNamespace();

        var adaptationSet = root.Descendants(ns + "AdaptationSet").FirstOrDefault();
        var representation = root.Descendants(ns + "Representation").FirstOrDefault()
            ?? throw new InvalidOperationException("DASH manifest is missing Representation element");

        var codecs = representation.Attribute("codecs")?.Value;
        var mimeType = adaptationSet?.Attribute("mimeType")?.Value
            ?? representation.Attribute("mimeType")?.Value;

        var template = representation.Element(ns + "SegmentTemplate");
        if (template != null)
        {
            return new ParsedDashManifest(codecs, mimeType, BuildFromSegmentTemplate(template, ns));
        }

        var segmentList = representation.Element(ns + "SegmentList");
        if (segmentList != null)
        {
            return new ParsedDashManifest(codecs, mimeType, BuildFromSegmentList(segmentList, ns));
        }

        var baseUrl = representation.Element(ns + "BaseURL")?.Value;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ParsedDashManifest(codecs, mimeType, new[] { baseUrl.Trim() });
        }

        throw new InvalidOperationException(
            "DASH manifest has no SegmentTemplate, SegmentList or BaseURL");
    }

    private static List<string> BuildFromSegmentTemplate(XElement template, XNamespace ns)
    {
        var init = template.Attribute("initialization")?.Value
            ?? throw new InvalidOperationException("SegmentTemplate missing initialization attribute");
        var media = template.Attribute("media")?.Value
            ?? throw new InvalidOperationException("SegmentTemplate missing media attribute");
        var startNumber = ParseIntAttribute(template, "startNumber", 1);

        var urls = new List<string> { init };

        var timeline = template.Element(ns + "SegmentTimeline");
        if (timeline != null)
        {
            var current = startNumber;
            foreach (var s in timeline.Elements(ns + "S"))
            {
                var repeat = ParseIntAttribute(s, "r", 0) + 1;
                for (var i = 0; i < repeat; i++)
                {
                    urls.Add(FillTemplate(media, current));
                    current++;
                }
            }
            return urls;
        }

        // Fixed-duration template (no SegmentTimeline) — derive count from MPD duration.
        var duration = ParseIntAttribute(template, "duration", 0);
        var timescale = ParseIntAttribute(template, "timescale", 1);
        if (duration <= 0 || timescale <= 0)
        {
            return urls;
        }

        var mpd = template.Ancestors(ns + "MPD").FirstOrDefault();
        var mediaDuration = ParseIsoDurationSeconds(mpd?.Attribute("mediaPresentationDuration")?.Value);
        if (mediaDuration <= 0)
        {
            return urls;
        }

        var segmentDurationSec = (double)duration / timescale;
        var count = (int)Math.Ceiling(mediaDuration / segmentDurationSec);
        for (var i = 0; i < count; i++)
        {
            urls.Add(FillTemplate(media, startNumber + i));
        }
        return urls;
    }

    private static List<string> BuildFromSegmentList(XElement segmentList, XNamespace ns)
    {
        var urls = new List<string>();
        var initElem = segmentList.Element(ns + "Initialization");
        var initUrl = initElem?.Attribute("sourceURL")?.Value;
        if (!string.IsNullOrEmpty(initUrl))
        {
            urls.Add(initUrl);
        }
        foreach (var seg in segmentList.Elements(ns + "SegmentURL"))
        {
            var url = seg.Attribute("media")?.Value;
            if (!string.IsNullOrEmpty(url))
            {
                urls.Add(url);
            }
        }
        return urls;
    }

    private static string FillTemplate(string template, int number)
    {
        var result = template.Replace("$Number$", number.ToString(CultureInfo.InvariantCulture));

        // Handle $Number%0Nd$ padding form
        const string fmtStart = "$Number%";
        var idx = result.IndexOf(fmtStart, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var end = result.IndexOf("d$", idx + fmtStart.Length, StringComparison.Ordinal);
            if (end > 0)
            {
                var fmt = result.Substring(idx + fmtStart.Length, end - (idx + fmtStart.Length));
                if (int.TryParse(fmt.TrimStart('0'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width))
                {
                    var padded = number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
                    result = result.Substring(0, idx) + padded + result.Substring(end + 2);
                }
            }
        }

        return result;
    }

    private static int ParseIntAttribute(XElement element, string name, int fallback)
    {
        var raw = element.Attribute(name)?.Value;
        if (string.IsNullOrEmpty(raw)) return fallback;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    // Parses ISO-8601 durations like "PT4M22.347S".
    private static double ParseIsoDurationSeconds(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('P')) return 0;
        var t = value.IndexOf('T');
        var span = t >= 0 ? value.AsSpan(t + 1) : ReadOnlySpan<char>.Empty;
        double seconds = 0;
        var num = "";
        foreach (var c in span)
        {
            if (char.IsDigit(c) || c == '.' || c == ',')
            {
                num += c == ',' ? '.' : c;
                continue;
            }
            if (num.Length == 0) continue;
            var n = double.Parse(num, CultureInfo.InvariantCulture);
            seconds += c switch
            {
                'H' => n * 3600,
                'M' => n * 60,
                'S' => n,
                _ => 0,
            };
            num = "";
        }
        return seconds;
    }
}
