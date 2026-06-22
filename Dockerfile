# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

ARG VERSION=0.0.0-dev
# Override on the server at build time, e.g. Liara mirror when nuget.org is blocked.
ARG NUGET_FEED=https://api.nuget.org/v3/index.json

COPY octo-fiesta.sln .
COPY octo-fiesta/octo-fiesta.csproj octo-fiesta/
COPY octo-fiesta.Tests/octo-fiesta.Tests.csproj octo-fiesta.Tests/

RUN printf '%s\n' \
    '<?xml version="1.0" encoding="utf-8"?>' \
    '<configuration>' \
    '  <packageSources>' \
    '    <clear />' \
    "    <add key=\"feed\" value=\"${NUGET_FEED}\" />" \
    '  </packageSources>' \
    '</configuration>' > /tmp/nuget.config \
    && dotnet restore --configfile /tmp/nuget.config

COPY octo-fiesta/ octo-fiesta/
COPY octo-fiesta.Tests/ octo-fiesta.Tests/

RUN dotnet publish octo-fiesta/octo-fiesta.csproj -c Release -p:Version=$VERSION -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

ARG CURL_IMPERSONATE_VERSION=1.5.1

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl zstd \
    && curl -fsSL "https://github.com/lexiforest/curl-impersonate/releases/download/v${CURL_IMPERSONATE_VERSION}/curl-impersonate-v${CURL_IMPERSONATE_VERSION}.x86_64-linux-gnu.tar.gz" \
    | tar xz -C /usr/local/bin \
    && rm -rf /var/lib/apt/lists/*

COPY docker/curl_amz_tls.sh /usr/local/bin/curl_amz_tls
RUN sed -i 's/\r$//' /usr/local/bin/curl_amz_tls && chmod +x /usr/local/bin/curl_amz_tls

RUN mkdir -p /app/downloads

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV SQUIDWTF_CURL_IMPERSONATE=/usr/local/bin/curl_amz_tls

ENTRYPOINT ["dotnet", "octo-fiesta.dll"]
