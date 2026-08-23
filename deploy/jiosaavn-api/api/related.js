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

  const token =
    permaUrl.split("/").pop() || "";

  return {
    id:
      token,

    name:
      decode(artist.name),

    image:
      artist.image || "",

    type:
      artist.type || "artist",

    artist_url:
      permaUrl,
  };
}

function formatAlbum(album) {
  const permaUrl =
    album.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  return {
    id:
      token,

    title:
      decode(album.title),

    subtitle:
      decode(album.subtitle),

    type:
      album.type,

    album_url:
      permaUrl,

    image:
      album.image || "",

    language:
      album.language,

    year:
      album.year,

    isExplicit:
      album.explicit_content === "1",

    song_count:
      album.more_info?.song_count || "0",

    artists: {
      primary:
        (
          album.more_info?.artistMap
            ?.primary_artists || []
        ).map(formatArtist),

      featured:
        (
          album.more_info?.artistMap
            ?.featured_artists || []
        ).map(formatArtist),
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
  
  const albumId =
    req.query.id;

  if (!albumId) {
    return res.status(400).json({
      status: false,
      message:
        "Missing album id",
    });
  }

  const endpoint =
    `https://www.jiosaavn.com/api.php` +
    `?__call=reco.getAlbumReco` +
    `&api_version=4` +
    `&_format=json` +
    `&_marker=0` +
    `&ctx=web6dot0` +
    `&albumid=${encodeURIComponent(albumId)}`;

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
            "https://www.jiosaavn.com/",

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

    return res.status(200).json({
      results:
        (data || [])
          .map(formatAlbum),
    });

  } catch (err) {
    return res.status(500).json({
      status: false,
      message: err.message,
    });
  }
}
