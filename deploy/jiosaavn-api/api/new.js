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

    name:
      decode(artist.name),

    image:
      artist.image || "",

    perma_url:
      artist.perma_url || "",
  };
}

function formatAlbum(album) {

  return {
    id:
      album.id,

    token:
      extractToken(
        album.perma_url
      ),

    title:
      decode(album.title),

    subtitle:
      decode(album.subtitle),

    type:
      "album",

    perma_url:
      album.perma_url,

    image:
      album.image,

    language:
      album.language,

    year:
      album.year,

    play_count:
      album.play_count || "",

    isExplicit:
      album.explicit_content === "1",

    more_info: {
      song_count:
        album.more_info
          ?.song_count || "0",

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
    },
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
      song.image,

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
        extractToken(albumUrl),

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
          ).map((artist) => ({
            id:
              artist.id,

            artist_token:
              extractToken(
                artist.perma_url
              ),

            name:
              decode(
                artist.name
              ),

            image:
              artist.image || "",

            perma_url:
              artist.perma_url || "",
          })),

        featured:
          (
            song.more_info?.artistMap
              ?.featured_artists || []
          ).map((artist) => ({
            id:
              artist.id,

            artist_token:
              extractToken(
                artist.perma_url
              ),

            name:
              decode(
                artist.name
              ),

            image:
              artist.image || "",

            perma_url:
              artist.perma_url || "",
          })),
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

  const lang =
    req.query.lang || "english";

  const page =
    req.query.page || "1";

  const endpoint =
    `https://www.jiosaavn.com/api.php` +
    `?__call=content.getAlbums` +
    `&api_version=4` +
    `&_format=json` +
    `&_marker=0` +
    `&n=50` +
    `&p=${page}` +
    `&ctx=web6dot0` +
    `&languages=${encodeURIComponent(lang)}`;

  try {

    const response =
      await fetch(endpoint, {
        headers: {
          accept:
            "application/json, text/plain, */*",

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

    const results =
      (data.data || [])
        .map((item) => {

          if (
            item.type === "album"
          ) {
            return formatAlbum(
              item
            );
          }

          if (
            item.type === "song"
          ) {
            return formatSong(
              item
            );
          }

          return null;

        })
        .filter(Boolean);

    return res.status(200).json({
      count:
        data.count || 0,

      last_page:
        data.last_page || false,

      results,
    });

  } catch (err) {

    return res.status(500).json({
      status: false,
      message:
        err.message,
    });
  }
}
