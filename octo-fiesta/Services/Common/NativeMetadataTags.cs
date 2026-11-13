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
            // iTunes advisory: 1 = explicit, 0 = none, 2 = clean edit. A false flag only says the
            // track is not explicit, not that it is an edited version, so 2 is never written.
            // Unknown writes nothing.
            if (song.IsExplicit.HasValue)
                vorbis.SetField("ITUNESADVISORY", song.IsExplicit.Value ? "1" : "0");
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
