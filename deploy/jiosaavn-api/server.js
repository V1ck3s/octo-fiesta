import express from "express";
import songs from "./api/songs.js";
import song from "./api/song.js";
import albums from "./api/albums.js";
import album from "./api/album.js";
import artists from "./api/artists.js";
import artist from "./api/artist.js";
import playlists from "./api/playlists.js";
import playlist from "./api/playlist.js";
import home from "./api/home.js";
import newReleases from "./api/new.js";
import related from "./api/related.js";
import image from "./api/image.js";

const app = express();
const port = process.env.PORT || 3000;

app.get("/", (_req, res) => {
  res.json({ status: "active", name: "JioSaavn API (self-hosted)" });
});

app.get("/api/songs", songs);
app.get("/api/song", song);
app.get("/api/albums", albums);
app.get("/api/album", album);
app.get("/api/artists", artists);
app.get("/api/artist", artist);
app.get("/api/playlists", playlists);
app.get("/api/playlist", playlist);
app.get("/api/home", home);
app.get("/api/new", newReleases);
app.get("/api/related", related);
app.get("/api/image", image);

app.listen(port, () => {
  console.log(`JioSaavn API (self-hosted) listening on port ${port}`);
});
