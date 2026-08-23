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
      artist.id || "",

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
    id:
      song.id,

    token,

    title:
      decode(song.title),

    subtitle:
      decode(song.subtitle),

    type:
      "song",

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
        decode(
          song.more_info?.album || ""
        ),

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
    },
  };
}

function formatAlbum(album) {
  const permaUrl =
    album.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  return {
    id:
      album.id,

    token,

    title:
      decode(album.title),

    type:
      "album",

    perma_url:
      permaUrl,

    image:
      album.image || "",

    language:
      album.language,

    isExplicit:
      album.explicit_content === "1",

    more_info: {
      release_date:
        album.more_info
          ?.release_date || "",

      artists:
        (
          album.more_info?.artistMap
            ?.artists || []
        ).map((artist) => ({
          id:
            artist.id,

          name:
            decode(
              artist.name
            ),

          role:
            artist.role || "",

          image:
            artist.image || "",

          type:
            artist.type || "artist",

          perma_url:
            artist.perma_url || "",
        })),
    },
  };
}

function formatPlaylist(playlist) {
  const permaUrl =
    playlist.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  return {
    id:
      playlist.id,

    token,

    title:
      decode(playlist.title),

    subtitle:
      decode(playlist.subtitle),

    type:
      "playlist",

    image:
      playlist.image || "",

    perma_url:
      permaUrl,

    more_info: {
      firstname:
        playlist.more_info
          ?.firstname || "",

      song_count:
        playlist.more_info
          ?.song_count || "0",

      follower_count:
        playlist.more_info
          ?.follower_count || "0",
    },
  };
}

function formatItem(item) {
  switch (item.type) {
    case "song":
      return formatSong(item);

    case "album":
      return formatAlbum(item);

    case "playlist":
      return formatPlaylist(item);

    case "artist":
    case "radio_station":
      return formatArtist(item);

    default:
      return null;
  }
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
  
  const lang =
    req.query.lang || "english";

  const endpoint =
    `https://www.jiosaavn.com/api.php` +
    `?__call=webapi.getLaunchData` +
    `&api_version=4` +
    `&_format=json` +
    `&_marker=0` +
    `&ctx=web6dot0`;

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
            `L=${lang}; ` +
            "mm_latlong=19.0760%2C72.8777; " +
            "geo=19.0760%2C72.8777%2CIN%2CMaharashtra%2CMumbai%2C400001",
        },
      });

    const data =
      await response.json();

    const modules =
      data.modules || {};

    const sortedModules =
      Object.entries(modules)
        .sort(
          (
            [, a],
            [, b]
          ) =>
            (a.position || 0) -
            (b.position || 0)
        );

    const finalModules =
      sortedModules
        .map(([key, module]) => {

          const rawItems =
            data[key];

          if (
            !Array.isArray(rawItems) ||
            !rawItems.length
          ) {
            return null;
          }

          const items =
            rawItems
              .map(formatItem)
              .filter(Boolean);

          if (!items.length) {
            return null;
          }

          return {
            id: key,

            title:
              decode(
                module.title || ""
              ),

            position:
              module.position || 0,

            items,
          };
        })
        .filter(Boolean);

    return res.status(200).json({
      language: lang,

      modules:
        finalModules,
    });

  } catch (err) {
    return res.status(500).json({
      status: false,
      message:
        err.message,
    });
  }
}
