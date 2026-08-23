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

function formatArtistResult(artist) {
  const permaUrl =
    artist.perma_url || "";

  const token =
    permaUrl.split("/").pop() || "";

  return {
    name:
      decode(artist.name),

    id:
      artist.id,

    token,

    ctr:
      Number(artist.ctr || 0),

    entity:
      Number(artist.entity || 0),

    image:
      artist.image || "",

    perma_url:
      permaUrl,

    type:
      artist.type,
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
    `&__call=search.getArtistResults`;

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
            `https://www.jiosaavn.com/search/artist/${encodeURIComponent(query)}`,

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
            item.type === "artist"
        )
        .map(formatArtistResult);

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
