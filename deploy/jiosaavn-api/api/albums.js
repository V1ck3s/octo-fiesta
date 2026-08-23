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

function formatAlbumArtist(artist) {
  return {
    id:
      artist.id || "",

    name:
      decode(artist.name),

    image:
      artist.image || "",

    perma_url:
      artist.perma_url || "",
  };
}

function formatAlbum(album) {
  const permaUrl =
    album.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  return {
    id: album.id,

    token,

    title:
      decode(album.title),

    subtitle:
      decode(album.subtitle),

    type:
      album.type,

    perma_url:
      permaUrl,

    image:
      album.image,

    language:
      album.language,

    year:
      album.year,

    play_count:
      album.play_count,

    isExplicit:
      album.explicit_content === "1",

    more_info: {
      song_count:
        album.more_info?.song_count || "0",

      artists: {
        primary:
          (
            album.more_info?.artistMap
              ?.primary_artists || []
          ).map(formatAlbumArtist),

        featured:
          (
            album.more_info?.artistMap
              ?.featured_artists || []
          ).map(formatAlbumArtist),
      },
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
    `&ctx=web6dot0` +
    `&n=${limit}` +
    `&__call=search.getAlbumResults`;

  try {
    const response =
      await fetch(endpoint, {
        method: "GET",

        headers: {
          accept:
            "application/json, text/plain, */*",

          "accept-language":
            "en-US,en;q=0.9",

          referer:
            `https://www.jiosaavn.com/search/album/${encodeURIComponent(query)}`,

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
            item.type === "album"
        )
        .map(formatAlbum);

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
