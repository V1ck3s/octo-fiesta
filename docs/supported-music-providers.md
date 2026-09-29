# Supported Music Providers

Set `MUSIC_SERVICE` to one provider, or to several separated by `,` (see [Multiple providers](../README.md#multiple-providers)).

| Provider (`MUSIC_SERVICE`) | Credentials | Max quality | Playlists | Setup |
|---|---|---|---|---|
| Deezer (default) | ARL token | FLAC 16-bit | Yes | [Getting Deezer credentials](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Deezer-Credentials-(ARL-Token)) |
| Qobuz | User ID + auth token | FLAC 24-bit/192kHz | Yes | [Getting Qobuz credentials](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Qobuz-Credentials-(User-ID-&-Token)) |
| Tidal | OAuth login (`--tidal-login`) | FLAC 24-bit/192kHz | Yes | [Getting Tidal credentials](https://github.com/V1ck3s/octo-fiesta/wiki/Getting-Tidal-Credentials-(OAuth-Tokens)) |
| Yandex | OAuth token | FLAC 16-bit | Yes | [Configuration](configuration.md#yandex-music) |
| GDStudio | none | up to FLAC 24-bit (source-dependent) | No | [Configuration](configuration.md#gdstudio) |
| AppleMusic | an [alacarte](https://github.com/sosjalapeno/alacarte) instance | ALAC / FLAC 24-bit/192kHz | Yes | [Configuration](configuration.md#apple-music-alacarte) |
| SquidWTF (deprecated) | none | source-dependent | Tidal only | [Configuration](configuration.md#squidwtf-deprecated) |

## Notes

- **GDStudio** aggregates several upstream sources (`netease`, `joox`, ...). Albums and artists are derived from search results and identified by name. The API is rate limited (about 50 requests per 5 minutes), and each configured source counts as its own request.
- **AppleMusic** needs octo-fiesta and alacarte to share the same music folder.
- **SquidWTF** is deprecated: the upstream squid.wtf services are down. It remains for self-hosted Tidal instances.
