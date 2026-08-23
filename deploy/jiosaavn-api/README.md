# jiosaavn-api (self-hosted)

A standalone Express wrapper around the request handlers from [ODSkyler/jiosaavn-api](https://github.com/ODSkyler/jiosaavn-api) (MIT licensed), so they can run as a regular Docker container instead of Vercel serverless functions.

## Why this exists

octo-fiesta's JioSaavn provider depends on an unofficial API that reformats JioSaavn's internal, undocumented endpoints into clean JSON. Public instances of these APIs are typically someone's personal Vercel/Cloudflare Workers deployment with no uptime guarantee — the two this fork depended on previously (`rtmx.vercel.app`, `sda.rhythmax.workers.dev`) both went offline without notice. Self-hosting removes that single point of failure.

## What's here

- `api/*.js` — the original handler files from ODSkyler/jiosaavn-api, unmodified. Each exports a `handler(req, res)` function.
- `server.js` — a small Express server that mounts each handler as a route. Vercel's serverless request/response objects are close enough to Express's that no changes to the handlers were needed.
- `Dockerfile` — Node 22 Alpine, `npm install`, run `server.js`.

## Endpoints used by octo-fiesta

- `GET /api/songs?q={query}` — search
- `GET /api/song?token={token}` — song details by JioSaavn's own song token (the last path segment of a song's `perma_url`)

The other routes (`albums`, `album`, `artists`, `artist`, `playlists`, `playlist`, `home`, `new`, `related`, `image`) are wired up for completeness but not currently called by octo-fiesta.

## Updating

If JioSaavn changes their internal API and these handlers break, check [ODSkyler/jiosaavn-api](https://github.com/ODSkyler/jiosaavn-api) for updates and re-copy the relevant file(s) from `api/`.
