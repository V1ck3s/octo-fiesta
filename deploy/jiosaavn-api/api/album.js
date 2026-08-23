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
      artist.id,

    artist_token:
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

function formatAlbumSong(song) {
  const permaUrl =
    song.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  const albumUrl =
    song.more_info?.album_url || "";

  const albumToken =
    albumUrl.split("/").pop() || "";

  return {
    id:
      song.id,

    token:
      token,

    title:
      decode(song.title),

    subtitle:
      decode(song.subtitle),

    type:
      "track",

    track_url:
      permaUrl,

    duration:
      song.more_info?.duration || "",

    image:
      song.image || "",

    language:
      song.language,

    year:
      song.year,

    isExplicit:
      song.explicit_content === "1",

    encrypted_media_url:
      song.more_info
        ?.encrypted_media_url || "",

    more_info: {
      album_id:
        albumToken,

      album:
        decode(
          song.more_info?.album || ""
        ),

      album_url:
        albumUrl,

      release_date:
        song.more_info
          ?.release_date || "",

      label:
        decode(
          song.more_info?.label || ""
        ),

      preview:
        song.more_info?.vlink || "",

      copyright:
        decode(
          song.more_info
            ?.copyright_text || ""
        ),
    },

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
    `&type=album` +
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

    const permaUrl =
      data.perma_url || "";

    const albumToken =
      permaUrl.split("/").pop() || token;

    return res.status(200).json({

      id:
        data.id,
      
      token:
        albumToken,

      title:
        decode(data.title),

      subtitle:
        decode(data.subtitle),

      header_desc:
        decode(
          data.header_desc || ""
        ),

      type:
        data.type,

      album_url:
        permaUrl,

      image:
        data.image || "",

      language:
        data.language,

      year:
        data.year,

      isExplicit:
        data.explicit_content === "1",

      song_count:
        data.list_count || "0",

      artists: {
        primary:
          (
            data.more_info?.artistMap
              ?.primary_artists || []
          ).map(formatArtist),

        featured:
          (
            data.more_info?.artistMap
              ?.featured_artists || []
          ).map(formatArtist),
      },

      copyright:
        decode(
          data.more_info
            ?.copyright_text || ""
        ),

      songs:
        (data.list || [])
          .map(formatAlbumSong),
    });

  } catch (err) {
    return res.status(500).json({
      status: false,
      message: err.message,
    });
  }
}
