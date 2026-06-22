#!/usr/bin/env bash
# Chrome 131 TLS/HTTP2 fingerprint only — no default Sec-Fetch/Accept headers.
# The app supplies per-request browser headers to avoid duplicate conflicting values
# (curl_chrome131 always adds Sec-Fetch-Mode: navigate, which breaks API POSTs).
dir=${0%/*}
exec "$dir/curl-impersonate" \
  --ciphers TLS_AES_128_GCM_SHA256:TLS_AES_256_GCM_SHA384:TLS_CHACHA20_POLY1305_SHA256:ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305:ECDHE-RSA-AES128-SHA:ECDHE-RSA-AES256-SHA:AES128-GCM-SHA256:AES256-GCM-SHA384:AES128-SHA:AES256-SHA \
  --curves X25519MLKEM768:X25519:P-256:P-384 \
  --split-cookies \
  --http2 \
  --http2-settings '1:65536;2:0;4:6291456;6:262144' \
  --http2-window-update 15663105 \
  --http2-stream-weight 256 \
  --http2-stream-exclusive 1 \
  --compressed \
  --ech true \
  --tlsv1.2 --alps --tls-permute-extensions \
  --cert-compression brotli \
  --tls-grease \
  --tls-signed-cert-timestamps \
  "$@"
