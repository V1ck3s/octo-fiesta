# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

ARG VERSION=0.0.0-dev

COPY octo-fiesta.sln .
COPY octo-fiesta/octo-fiesta.csproj octo-fiesta/
COPY octo-fiesta.Tests/octo-fiesta.Tests.csproj octo-fiesta.Tests/

COPY octo-fiesta/ octo-fiesta/
COPY octo-fiesta.Tests/ octo-fiesta.Tests/

RUN dotnet publish octo-fiesta/octo-fiesta.csproj -c Release -p:Version=$VERSION -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

ARG CURL_IMPERSONATE_VERSION=0.6.1

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && curl -fsSL "https://github.com/lwthiker/curl-impersonate/releases/download/v${CURL_IMPERSONATE_VERSION}/curl-impersonate-v${CURL_IMPERSONATE_VERSION}.x86_64-linux-gnu.tar.gz" \
    | tar xz -C /usr/local/bin \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/downloads

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV SQUIDWTF_CURL_IMPERSONATE=/usr/local/bin/curl_chrome116

ENTRYPOINT ["dotnet", "octo-fiesta.dll"]
