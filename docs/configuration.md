# Configuration

Every setting is an environment variable. With Docker Compose, set the `.env` names below (see [`.env.example`](../.env.example)); `docker-compose.yml` passes each to the app as the `Section__Key` name shown in the second column. You can also set the `Section__Key` form directly.

## General

| `.env` | App setting | Default | Description |
|---|---|---|---|
| `SUBSONIC_URL` | `Subsonic__Url` | `http://localhost:4533` | Navidrome/Subsonic server URL. |
| `SUBSONIC_ADMIN_USERNAME` / `SUBSONIC_ADMIN_PASSWORD` | `Subsonic__AdminUsername` / `AdminPassword` | empty | Admin account for actions that need it (optional). |
| `MUSIC_SERVICE` | `Subsonic__MusicService` | `Deezer` | Provider(s): `Deezer`, `Qobuz`, `Tidal`, `Yandex`, `GDStudio`, `AppleMusic`, `SquidWTF`. Comma separate several, e.g. `Deezer,GDStudio`. |
| `DOWNLOAD_PATH` | `Library__DownloadPath` | `./downloads` | Host folder mounted at `/app/downloads`. |
| `<Provider>__DownloadPath` | same | unset | Per-provider override of the shared path. Set as a plain environment variable and only when needed; an empty value would override the shared path. |
| `STORAGE_MODE` | `Subsonic__StorageMode` | `Permanent` | `Permanent` saves to the library; `Cache` keeps temporary files. |
| `CACHE_DURATION_HOURS` | `Subsonic__CacheDurationHours` | `1` | Cache lifetime when `STORAGE_MODE=Cache`. |
| `DOWNLOAD_MODE` | `Subsonic__DownloadMode` | `Track` | `Track` or `Album` (download the whole album when a track is played). |
| `ENABLE_EXTERNAL_PLAYLISTS` | `Subsonic__EnableExternalPlaylists` | `true` | Search and download provider playlists. |
| `PLAYLISTS_DIRECTORY` | `Subsonic__PlaylistsDirectory` | `playlists` | Folder for generated `.m3u` files. |
| `EXPLICIT_FILTER` | `Subsonic__ExplicitFilter` | `All` | `All`, `ExplicitOnly`, `CleanOnly` (Deezer only). |
| `AUTO_UPGRADE_QUALITY` | `Subsonic__AutoUpgradeQuality` | `false` | Re-download a track when the provider offers better quality. |
| `ALLOW_BITRATE_UPGRADE` | `Subsonic__AllowBitrateUpgrade` | `false` | When a download would land on an existing file name: `false` skips it and keeps the file; `true` downloads only if the new bitrate is higher, then replaces the file. |
| `DISABLE_LIBRARY_SCAN` | `Subsonic__DisableLibraryScan` | `false` | Do not trigger a library scan after a download. |
| `FOLDER_TEMPLATE` | `Subsonic__FolderTemplate` | `{artist}/{album}/{track} - {title}` | Placeholders: `{artistLetter}`, `{artist}`, `{album}`, `{title}`, `{track}`, `{disc}`, `{year}`, `{genre}`, `{quality}`. |
| `INTERNAL_PORT` | `ASPNETCORE_URLS` | `8080` | Port inside the container. |
| `CONFIG_PATH` | (volume) | `./config` | Host folder mounted at `/config` (Tidal token store). |

### Navidrome upload API (forked Navidrome only)

| `.env` | App setting | Default | Description |
|---|---|---|---|
| `USE_NAVIDROME_UPLOAD_API` | `Subsonic__UseNavidromeUploadApi` | `false` | Upload downloads via `POST /api/upload` instead of saving to a folder and scanning. |
| `NAVIDROME_LIBRARY_ID` | `Subsonic__NavidromeLibraryId` | `1` | Library to upload into. |
| `NAVIDROME_UPLOAD_FOLDER` | `Subsonic__NavidromeUploadFolder` | empty | Folder prefix inside the library. |

### External cover indicator

| `.env` | App setting | Default | Description |
|---|---|---|---|
| `EXTERNAL_COVER_INDICATOR_SIZE` | `ExternalCover__IndicatorSize` | `1` | `0` off, `1` normal, `2` double. |
| `EXTERNAL_COVER_INDICATOR_SATURATION` | `ExternalCover__IndicatorSaturation` | `1` | `0` unchanged, `1` desaturated, `2` stronger. |
| `EXTERNAL_COVER_INDICATOR_COLOR` | `ExternalCover__IndicatorColor` | `0` | `0` frosted blur, `1` invert, or a 6-digit hex without `#`. |

## Deezer

| `.env` | App setting | Description |
|---|---|---|
| `DEEZER_ARL` | `Deezer__Arl` | ARL token (required). |
| `DEEZER_ARL_FALLBACK` | `Deezer__ArlFallback` | Fallback ARL (optional). |
| `DEEZER_QUALITY` | `Deezer__Quality` | `FLAC`, `MP3_320`, `MP3_128`; default is the best available. |

## Qobuz

| `.env` | App setting | Description |
|---|---|---|
| `QOBUZ_USER_ID` / `QOBUZ_USER_AUTH_TOKEN` | `Qobuz__UserId` / `Qobuz__UserAuthToken` | Credentials (required). |
| `QOBUZ_QUALITY` | `Qobuz__Quality` | `FLAC`, `FLAC_24_HIGH`, `FLAC_24_LOW`, `FLAC_16`, `MP3_320`. |
| `QOBUZ_APP_ID` / `QOBUZ_APP_SECRET` | `Qobuz__AppId` / `Qobuz__AppSecret` | When both are set, the web bundle is not scraped. |

## Tidal

Run the login once: `docker compose run --rm octo-fiesta --tidal-login`.

| `.env` | App setting | Description |
|---|---|---|
| `TIDAL_TOKEN_STORE` | `Tidal__TokenStore` | Token file, default `/config/tidal-tokens.json`. Keep it on a volume. |
| `TIDAL_QUALITY` | `Tidal__Quality` | `HI_RES_LOSSLESS`, `LOSSLESS`, `HIGH`, `LOW`. |
| `TIDAL_CLIENT_ID` / `TIDAL_CLIENT_SECRET` | `Tidal__ClientId` / `ClientSecret` | Override the client used (optional). |
| `TIDAL_ACCESS_TOKEN`, `TIDAL_REFRESH_TOKEN`, `TIDAL_USER_ID`, `TIDAL_COUNTRY_CODE` | `Tidal__*` | Tokens obtained elsewhere; take precedence over the store. |

## Yandex Music

| `.env` | App setting | Default | Description |
|---|---|---|---|
| `YANDEX_OAUTH_TOKEN` | `Yandex__OAuthToken` | | Token from the `#access_token` fragment after [authorizing](https://oauth.yandex.ru/authorize?response_type=token&client_id=23cabbbdc6cd418abb4b39c32c41195d). |
| `YANDEX_QUALITY` | `Yandex__Quality` | `FLAC` | `AAC_64`, `MP3_192`, `AAC_192`, `AAC_256`, `MP3_320`, `FLAC`. |
| `YANDEX_LANGUAGE` | `Yandex__Language` | `ru` | `en`, `uz`, `uk`, `us`, `ru`, `kk`, `hy`. |
| `YANDEX_INCLUDE_UNAVAILABLE_TRACKS` | `Yandex__IncludeUnavailable` | `false` | Show tracks marked unavailable. |

## GDStudio

[GD Studio](https://music-api.gdstudio.xyz) needs no credentials. Set `MUSIC_SERVICE=GDStudio` (or add it to a list). Tracks, albums and artists are supported; playlists are not.

| `.env` | App setting | Default | Description |
|---|---|---|---|
| `GDSTUDIO_SOURCE` | `GDStudio__Source` | `netease` | Upstream source(s), comma separated, e.g. `netease,joox`. Not validated: any value the API accepts works. |
| `GDSTUDIO_TIMEOUT_SECONDS` | `GDStudio__TimeoutSeconds` | `15` | Per-source timeout for search/metadata calls. |
| `GDSTUDIO_BR` | `GDStudio__Br` | `999` | Quality: `128`, `192`, `320`, `740` (16-bit lossless), `999` (24-bit lossless). |
| `GDSTUDIO_API` | `GDStudio__Api` | `https://music-api.gdstudio.xyz/api.php` | API endpoint. |
| `GDSTUDIO_PROXY` | `GDStudio__Proxy` | empty | `http://`, `https://` or `socks5://` proxy for API and downloads. |

With several sources, each is queried separately and in parallel (N sources means N requests per search, with no added delay) and the results are interleaved. A source that fails or exceeds the timeout is logged as an error and skipped; the rest still return. Track ids carry their source as `<source>~<id>`.

If the requested `br` is unsupported or returns nothing, the next lower value is tried once (`999` falls back to `740` only; `128` has no fallback).

## Apple Music (alacarte)

Apple Music is served by a running [alacarte](https://github.com/sosjalapeno/alacarte) instance, which downloads into the music folder octo-fiesta uses. In alacarte, turn on **Settings → octo-fiesta Integration** and copy the two values it shows.

| `.env` | App setting | Default | Description |
|---|---|---|---|
| `APPLEMUSIC_ALACARTE_URL` | `AppleMusic__AlacarteUrl` | | alacarte base URL, e.g. `http://alacarte-host:7373`. |
| `APPLEMUSIC_API_TOKEN` | `AppleMusic__ApiToken` | | alacarte integration token. |
| `APPLEMUSIC_DOWNLOAD_TIMEOUT_SECONDS` | `AppleMusic__DownloadTimeoutSeconds` | `900` | Wait for alacarte to finish one song. |
| (plain env) | `AppleMusic__DownloadPath` | unset | Use when alacarte's folder differs from `DOWNLOAD_PATH`. It must still resolve to the exact folder alacarte writes into. |

Add `AppleMusic` to `MUSIC_SERVICE` (e.g. `Deezer,AppleMusic`) or use it alone.

## SquidWTF (deprecated)

| `.env` | App setting | Default | Description |
|---|---|---|---|
| `SQUIDWTF_SOURCE` | `SquidWTF__Source` | `Qobuz` | `Qobuz` or `Tidal`. |
| `SQUIDWTF_QUALITY` | `SquidWTF__Quality` | best | Qobuz: `27`, `7`, `6`, `5`. Tidal: `HI_RES_LOSSLESS`, `LOSSLESS`, `HIGH`, `LOW`. |
| `SQUIDWTF_INSTANCE_TIMEOUT` | `SquidWTF__InstanceTimeoutSeconds` | `5` | Tidal backend: switch instance after this many seconds. |
| `SQUIDWTF_INSTANCE` | `SquidWTF__Instances__0` | | Force one Tidal API instance (skips the remote list). |
| `SQUIDWTF_INSTANCES_URL` | `SquidWTF__InstancesUrl` | | Override the remote instances list URL. |
