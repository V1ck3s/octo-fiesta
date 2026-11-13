# Qobuz metadata and explicit preference

Set `EXPLICIT_FILTER=PreferExplicit` in the compose `.env`, or
`Subsonic__ExplicitFilter=PreferExplicit` when configuring the container directly.
The default remains `All`.

`PreferExplicit` stably ranks Qobuz track and album search results flagged with
`parental_warning` first. All non-explicit and unknown results remain available
as fallback. It ranks the returned search page; it does not search additional
pages, replace a requested track ID, substitute an album edition, or reorder
album tracks and playlists. It affects Qobuz only.

Track advisory comes only from that track's `parental_warning` (boolean or numeric
0/1). An album advisory is used only to rank album search results, never to infer
an individual track's status. Missing, null, or unrecognized values remain unknown.
`ExplicitOnly` and `CleanOnly` exclude known contrary track flags and retain unknown
tracks. A false flag means the provider reports non-explicit; it does not prove a
track is an edited version. No Apple/iTunes service or MusicBrainz lookup is used.

Qobuz metadata is preserved through search, track, album-track, and playlist
parsing. FLAC downloads receive native `ISRC`, `BARCODE` (album UPC), `LABEL`, and,
when known, `ITUNESADVISORY` (1 explicit / 0 not explicit). Copyright continues
through the existing copyright tag writer. MP3 downloads receive ID3 `TSRC`,
`BARCODE`, and `TPUB`; this change does not add MP3 advisory tags. Existing ISRC
comments remain for compatibility. Unknown advisory writes no advisory field.
`ITUNESADVISORY` is a tag convention; its value here comes from Qobuz. Its value 2
means a clean edit, which `parental_warning` cannot show, so it is never written.
OpenSubsonic responses carry `explicitStatus: "explicit"` for explicit tracks and
omit it otherwise; `"clean"` is never emitted for the same reason.

Qobuz library discovery and search-result deduplication require matching ISRCs
before treating a local file as the requested recording. Known contrary advisory
also rejects the match. Existing provider-ID file mappings remain authoritative.
Older library files without ISRC may remain visible alongside Qobuz results and
may be downloaded separately if explicitly selected. They are not retagged by
this change. Existing download collision handling selects a unique output path.

Validation uses mocked provider responses and temporary silent FLAC fixtures.
No credentials or downloaded media are committed. Production rollout is separate;
this branch does not replace a deployed image or bulk modify a library.
