using octo_fiesta.Models.Domain;

namespace octo_fiesta.Services.Common;

/// <summary>Format-specific recording identifiers and provider advisory tags.</summary>
internal static class NativeMetadataTags
{
    internal static void Write(TagLib.File file, Song song)
    {
        if (file is TagLib.Flac.File && file.GetTag(TagLib.TagTypes.Xiph, true) is TagLib.Ogg.XiphComment vorbis)
        {
            if (!string.IsNullOrWhiteSpace(song.Isrc)) vorbis.SetField("ISRC", song.Isrc);
            if (!string.IsNullOrWhiteSpace(song.Barcode)) vorbis.SetField("BARCODE", song.Barcode);
            if (!string.IsNullOrWhiteSpace(song.Label)) vorbis.SetField("LABEL", song.Label);
            // 1 = explicit, 2 = non-explicit. Unknown must not become 0/clean.
            if (song.IsExplicit.HasValue)
                vorbis.SetField("ITUNESADVISORY", song.IsExplicit.Value ? "1" : "2");
        }
        if (file is TagLib.Mpeg.AudioFile && file.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3)
        {
            if (!string.IsNullOrWhiteSpace(song.Isrc))
                TagLib.Id3v2.TextInformationFrame.Get(id3, "TSRC", true).Text = new[] { song.Isrc };
            if (!string.IsNullOrWhiteSpace(song.Barcode))
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, "BARCODE", true).Text = new[] { song.Barcode };
            if (!string.IsNullOrWhiteSpace(song.Label))
                TagLib.Id3v2.TextInformationFrame.Get(id3, "TPUB", true).Text = new[] { song.Label };
        }
    }
}
