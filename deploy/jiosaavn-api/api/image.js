export const config = {
  regions: ["bom1"]
};

export default async function handler(
  req,
  res
) {

  res.setHeader(
    "Access-Control-Allow-Origin",
    "*"
  );

  try {

    const imageUrl =
      req.query.url;

    if (!imageUrl) {
      return res.status(400).json({
        status: false,
        message:
          "Missing image url",
      });
    }

    const response =
      await fetch(imageUrl, {
        headers: {
          referer:
            "https://www.jiosaavn.com/",

          "user-agent":
            "Mozilla/5.0",
        },
      });

    if (!response.ok) {
      return res.status(500).json({
        status: false,
        message:
          "Failed to fetch image",
      });
    }

    const contentType =
      response.headers.get(
        "content-type"
      ) || "image/jpeg";

    const buffer =
      Buffer.from(
        await response.arrayBuffer()
      );

    res.setHeader(
      "Content-Type",
      contentType
    );

    res.setHeader(
      "Cache-Control",
      "public, max-age=31536000, immutable"
    );

    res.setHeader(
      "Cross-Origin-Resource-Policy",
      "cross-origin"
    );

    res.setHeader(
      "Cross-Origin-Embedder-Policy",
       "credentialless"
    );

    return res.send(buffer);

  } catch (err) {

    return res.status(500).json({
      status: false,
      message:
        err.message,
    });
  }
}
