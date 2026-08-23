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

function formatDetailedArtist(artist) {
  const permaUrl =
    artist.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  return {
    id:
      artist.id,

    token,

    name:
      decode(artist.name),

    image:
      artist.image || "",

    perma_url:
      permaUrl,
  };
}

function formatPlaylistSong(song) {
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

    token,

    title:
      decode(song.title),

    subtitle:
      decode(song.subtitle),

    type:
      song.type,

    perma_url:
      permaUrl,

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
      album_id:
        song.more_info?.album_id || "",

      album_token:
        albumToken,

      album:
        decode(song.more_info?.album || ""),

      label:
        decode(song.more_info?.label || ""),

      label_id:
        song.more_info?.label_id || "",

      origin:
        song.more_info?.origin || "",

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
          ).map(formatDetailedArtist),

        featured:
          (
            song.more_info?.artistMap
              ?.featured_artists || []
          ).map(formatDetailedArtist),
      },

      release_date:
        song.more_info
          ?.release_date || null,

      label_url:
        song.more_info?.label_url || "",
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

  const limit =
    req.query.n || "50";

  const page =
    req.query.p || "1";

  const endpoint =
    `https://www.jiosaavn.com/api.php` +
    `?__call=webapi.get` +
    `&token=${encodeURIComponent(token)}` +
    `&type=playlist` +
    `&p=${page}` +
    `&n=${limit}` +
    `&includeMetaTags=0` +
    `&ctx=wap6dot0` +
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

          "x-requested-with":
            "XMLHttpRequest",

          "accept-language":
            "en-US,en;q=0.9",

          referer:
            "https://www.jiosaavn.com/",

          origin:
            "https://www.jiosaavn.com",

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
      id:
        data.id,

      token,

      title:
        decode(data.title),

      subtitle:
        decode(data.subtitle),

      header_desc:
        decode(data.header_desc),

      type:
        data.type,

      perma_url:
        data.perma_url,

      image:
        (data.image || "")
          .split("?")[0],

      more_info: {
        firstname:
          data.more_info
            ?.firstname || "",

        subtitle_desc:
          data.more_info
            ?.subtitle_desc || [],
      },

      list_count:
        data.list_count || "0",

      list:
        (data.list || []).map(
          formatPlaylistSong
        ),
    });

  } catch (err) {
    return res.status(500).json({
      status: false,
      message: err.message,
    });
  }
}
