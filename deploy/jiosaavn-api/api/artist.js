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
    id: token,

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
    id: token,

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
        song.year || "",

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

function formatAlbum(album) {
  const permaUrl =
    album.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  return {
  id: album.id,
    
  token,

  title: decode(album.title),

  subtitle: decode(album.subtitle),

  type: album.type,

  perma_url: permaUrl,

  image: album.image || "",

  language: album.language,

  year: album.year,

  isExplicit:
    album.explicit_content === "1",

  song_count:
    album.more_info?.song_count || "0",

  artists: {
    primary:
      (
        album.more_info?.artistMap?.primary_artists || []
      ).map(formatArtist),

    featured:
      (
        album.more_info?.artistMap?.featured_artists || []
      ).map(formatArtist),
  },
 };
}

function formatPlaylist(playlist) {
  const permaUrl =
    playlist.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  return {
    id: token,

    title:
      decode(playlist.title),

    subtitle:
      decode(playlist.subtitle),

    type:
      playlist.type,

    image:
      playlist.image || "",

    url:
      permaUrl,

    more_info: {
      firstname:
        playlist.more_info
          ?.firstname || "",

      type:
        playlist.more_info
          ?.entity_type || "",

      lastname:
        playlist.more_info
          ?.lastname || "",

      song_count:
        playlist.more_info
          ?.song_count || "0",

      language:
        playlist.more_info
          ?.language || "",
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

  const page =
    req.query.page || "1";

  if (!token) {
    return res.status(400).json({
      status: false,
      message:
        "Missing token",
    });
  }

  const popularEndpoint =
    `https://www.jiosaavn.com/api.php` +
    `?__call=webapi.get` +
    `&token=${encodeURIComponent(token)}` +
    `&type=artist` +
    `&p=${page}` +
    `&n_song=50` +
    `&n_album=50` +
    `&sub_type=` +
    `&category=` +
    `&sort_order=asc` +
    `&includeMetaTags=0` +
    `&ctx=web6dot0` +
    `&api_version=4` +
    `&_format=json` +
    `&_marker=0`;

  const latestEndpoint =
    `https://www.jiosaavn.com/api.php` +
    `?__call=webapi.get` +
    `&token=${encodeURIComponent(token)}` +
    `&type=artist` +
    `&p=${page}` +
    `&n_song=50` +
    `&n_album=50` +
    `&sub_type=` +
    `&category=latest` +
    `&sort_order=desc` +
    `&includeMetaTags=0` +
    `&ctx=web6dot0` +
    `&api_version=4` +
    `&_format=json` +
    `&_marker=0`;

  try {
    const headers = {
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
    };

    const [
      popularResponse,
      latestResponse
    ] = await Promise.all([

      fetch(popularEndpoint, {
        headers
      }),

      fetch(latestEndpoint, {
        headers
      })
    ]);

    const popularData =
      await popularResponse.json();

    const latestData =
      await latestResponse.json();

    return res.status(200).json({
      id: token,

      name:
        decode(popularData.name),

      subtitle:
        decode(
          popularData.subtitle
        ),

      image:
        popularData.image || "",

      topSongs:
        (
          popularData.topSongs || []
        ).map(formatSong),

      topAlbums:
        (
         latestData.topAlbums?.length
         ? latestData.topAlbums
         : popularData.topAlbums || []
        ).map(formatAlbum),

      singles:
        (
         latestData.singles?.length
         ? latestData.singles
         : popularData.singles || []
        ).map(formatAlbum),

      latest_release:
        (
         latestData.latest_release?.length
         ? latestData.latest_release
         : popularData.latest_release || []
        ).map(formatAlbum),

      dedicated_artist_playlist:
        (
         latestData.dedicated_artist_playlist?.length
         ? latestData.dedicated_artist_playlist
         : popularData.dedicated_artist_playlist || []
        ).map(formatPlaylist),

      featured_artist_playlist:
        (
         latestData.featured_artist_playlist?.length
         ? latestData.featured_artist_playlist
         : popularData.featured_artist_playlist || []
        ).map(formatPlaylist),
    });
  } catch (err) {
    return res.status(500).json({
      status: false,
      message: err.message,
    });
  }
}
