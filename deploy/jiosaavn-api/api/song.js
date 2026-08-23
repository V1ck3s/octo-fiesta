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

function extractToken(url = "") {
  return url.split("/").pop() || "";
}

function formatArtist(artist) {
  return {
    id:
      artist.id,

    token:
      extractToken(
        artist.perma_url
      ),

    name:
      decode(artist.name),

    image:
      artist.image || "",

    perma_url:
      artist.perma_url || "",
  };
}

function formatCreditArtist(
  artist
) {
  return {
    id:
      artist.id,

    token:
      extractToken(
        artist.perma_url
      ),

    name:
      decode(artist.name),

    role:
      artist.role || "",

    perma_url:
      artist.perma_url || "",
  };
}

function formatSong(song) {
  const albumUrl =
    song.more_info?.album_url || "";

  return {
    id:
      song.id,

    token:
      extractToken(
        song.perma_url
      ),

    title:
      decode(song.title),

    subtitle:
      decode(song.subtitle),

    type:
      "song",

    perma_url:
      song.perma_url,

    image:
      song.image || "",

    language:
      song.language,

    year:
      song.year,

    play_count:
      song.play_count,

    isExplicit:
      song.explicit_content === "1",

    more_info: {
      music:
        decode(
          song.more_info?.music || ""
        ),

      album_id:
        song.more_info?.album_id || "",

      album_token:
        extractToken(albumUrl),

      album:
        decode(
          song.more_info?.album || ""
        ),

      label:
        decode(
          song.more_info?.label || ""
        ),

      encrypted_media_url:
        song.more_info
          ?.encrypted_media_url || "",

      album_url:
        albumUrl,

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

        credits:
          (
            song.more_info?.artistMap
              ?.artists || []
          ).map(formatCreditArtist),
      },

      release_date:
        song.more_info
          ?.release_date || "",

      label_url:
        song.more_info?.label_url || "",

      vcode:
        song.more_info?.vcode || "",

      vlink:
        song.more_info?.vlink || "",
    },
  };
}

function formatModuleSong(song) {
  return formatSong(song);
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
  
  const token =
    req.query.token;

  if (!token) {
    return res.status(400).json({
      status: false,
      message:
        "Missing token parameter",
    });
  }

  const endpoint =
    `https://www.jiosaavn.com/api.php` +
    `?__call=webapi.get` +
    `&token=${encodeURIComponent(token)}` +
    `&type=song` +
    `&includeMetaTags=0` +
    `&ctx=web6dot0` +
    `&api_version=4` +
    `&_format=json` +
    `&_marker=0`;

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

    const song =
      data.songs?.[0];

    if (!song) {
      return res.status(404).json({
        status: false,
        message:
          "Song not found",
      });
    }

    return res.status(200).json(
  formatSong(song)
);

  } catch (err) {
    return res.status(500).json({
      status: false,
      message:
        err.message,
    });
  }
}
