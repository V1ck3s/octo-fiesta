export const config = {
  regions: ["bom1"]
};

function decode(text) {
  if (!text) return "";

  return text
    .replace(/&amp;/g, "&")
    .replace(/&#039;/g, "'")
    .replace(/&quot;/g, '"');
}

function formatArtist(artist) {
  const permaUrl =
    artist.perma_url || "";

  const artistToken =
    permaUrl.split("/").pop() || "";

  return {
    id: artist.id,

    artist_token:
      artistToken,

    name:
      decode(artist.name),

    image:
      artist.image || "",

    perma_url:
      permaUrl,
  };
}

function formatSong(song) {
  const permaUrl =
    song.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  const albumUrl =
    song.more_info?.album_url || "";

  const albumToken =
    albumUrl.split("/").pop() || "";

  return {
    id: song.id,

    token,

    title: decode(song.title),

    subtitle: decode(song.subtitle),

    type: song.type,

    perma_url: permaUrl,

    image: song.image,

    language: song.language,

    year: song.year,

    play_count: song.play_count,

    isExplicit:
      song.explicit_content === "1",

    more_info: {
      album_id:
        song.more_info?.album_id || "",

      album_token:
        albumToken,

      album:
        decode(song.more_info?.album || ""),

      album_url:
        albumUrl,

      encrypted_media_url:
        song.more_info
          ?.encrypted_media_url || "",

      duration:
        song.more_info?.duration || "",

      copyright_text:
        decode(
          song.more_info
            ?.copyright_text || ""
        ),

      artists: {
        primary:
          (
            song.more_info?.artistMap
              ?.primary_artists || []
          ).map(formatArtist),

        featured:
          (
            song.more_info?.artistMap
              ?.featured_artists || []
          ).map(formatArtist),
      },

      release_date:
        song.more_info
          ?.release_date || null,

      vcode:
        song.more_info?.vcode || "",

      vlink:
        song.more_info?.vlink || "",
    },
  };
}

export default async function handler(
  req,
  res
) {

    res.setHeader(
    "Access-Control-Allow-Origin",
    "*"
  );

  res.setHeader(
    "Access-Control-Allow-Methods",
    "GET, OPTIONS"
  );

  res.setHeader(
    "Access-Control-Allow-Headers",
    "*"
  );

  if (req.method === "OPTIONS") {
    return res.status(200).end();
  }
  
  const query =
    req.query.q;

  if (!query) {
    return res.status(400).json({
      status: false,
      message:
        "Missing q parameter",
    });
  }

  const limit =
    req.query.n || "20";

  const page =
    req.query.p || "1";

  const endpoint =
    `https://www.jiosaavn.com/api.php` +
    `?p=${page}` +
    `&q=${encodeURIComponent(query)}` +
    `&_format=json` +
    `&_marker=0` +
    `&api_version=4` +
    `&ctx=wap6dot0` +
    `&n=${limit}` +
    `&__call=search.getResults`;

  try {
    const response =
      await fetch(endpoint, {
        headers: {
          accept:
            "application/json, text/plain, */*",

          "x-requested-with":
            "XMLHttpRequest",

          "accept-language":
            "en-US,en;q=0.9",

          referer:
            `https://www.jiosaavn.com/search/song/${encodeURIComponent(query)}`,

          "user-agent":
            "Mozilla/5.0",

          cookie:
            "DL=english; " +
            "L=english; " +
            "mm_latlong=19.0760%2C72.8777; " +
            "geo=19.0760%2C72.8777%2CIN%2CMaharashtra%2CMumbai%2C400001",
        },
      });

    const data =
      await response.json();

    const results =
      (data.results || [])
        .filter(
          (item) =>
            item.type === "song"
        )
        .map(formatSong);

    return res.status(200).json({
      total: Number(
        data.total || 0
      ),

      start: Number(
        data.start || 0
      ),

      results,
    });

  } catch (err) {
    return res.status(500).json({
      status: false,
      message: err.message,
    });
  }
}
