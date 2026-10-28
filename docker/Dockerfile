# syntax=docker/dockerfile:1

# ---- SPA build ----
FROM --platform=$BUILDPLATFORM node:22-alpine AS spa
WORKDIR /spa
COPY src/ANalyze.Web/ClientApp/package.json src/ANalyze.Web/ClientApp/package-lock.json ./
RUN npm ci
COPY VERSION /VERSION
COPY src/ANalyze.Web/ClientApp/ ./
# Builds into ../wwwroot (= /wwwroot here).
RUN APP_VERSION="$(cat /VERSION)" npm run build && test -f /wwwroot/index.html

# ---- .NET build ----
# Runs natively on the build host and cross-targets the image architecture (no emulation).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY VERSION RELEASE_NOTES.md Directory.Build.props Directory.Packages.props ANalyze.slnx ./
COPY src/ANalyze.Domain/ANalyze.Domain.csproj src/ANalyze.Domain/
COPY src/ANalyze.Infrastructure/ANalyze.Infrastructure.csproj src/ANalyze.Infrastructure/
COPY src/ANalyze.Scanner/ANalyze.Scanner.csproj src/ANalyze.Scanner/
COPY src/ANalyze.Preview/ANalyze.Preview.csproj src/ANalyze.Preview/
COPY src/ANalyze.Ai/ANalyze.Ai.csproj src/ANalyze.Ai/
COPY src/ANalyze.Actions/ANalyze.Actions.csproj src/ANalyze.Actions/
COPY src/ANalyze.Integrations/ANalyze.Integrations.csproj src/ANalyze.Integrations/
COPY src/ANalyze.Integrations.Plex/ANalyze.Integrations.Plex.csproj src/ANalyze.Integrations.Plex/
COPY src/ANalyze.Integrations.Arr/ANalyze.Integrations.Arr.csproj src/ANalyze.Integrations.Arr/
COPY src/ANalyze.Integrations.Downloads/ANalyze.Integrations.Downloads.csproj src/ANalyze.Integrations.Downloads/
COPY src/ANalyze.Integrations.Jellyfin/ANalyze.Integrations.Jellyfin.csproj src/ANalyze.Integrations.Jellyfin/
COPY src/ANalyze.Integrations.Photos/ANalyze.Integrations.Photos.csproj src/ANalyze.Integrations.Photos/
COPY src/ANalyze.Integrations.Docker/ANalyze.Integrations.Docker.csproj src/ANalyze.Integrations.Docker/
COPY src/ANalyze.Web/ANalyze.Web.csproj src/ANalyze.Web/
RUN dotnet restore src/ANalyze.Web/ANalyze.Web.csproj -a $TARGETARCH
COPY src/ src/
COPY --from=spa /wwwroot/ src/ANalyze.Web/wwwroot/
RUN dotnet publish src/ANalyze.Web/ANalyze.Web.csproj -c Release -a $TARGETARCH --self-contained false -o /app -p:SkipSpaBuild=true --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# ffmpeg gives video thumbnails and poster frames; build with --build-arg FFMPEG=false for a smaller image.
ARG FFMPEG=true
RUN apt-get update \
    && apt-get install -y --no-install-recommends fontconfig libfontconfig1 \
    && if [ "$FFMPEG" = "true" ]; then apt-get install -y --no-install-recommends ffmpeg; fi \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ANalyze__DataDir=/data \
    DOTNET_gcServer=0
RUN mkdir -p /data /library
VOLUME ["/data"]
EXPOSE 8080
# Runs as root on purpose: it must read (and, when allowed, move or delete) files owned by any user on the
# mounted shares. Folders it creates take the owner of their parent folder. Mount roots :ro for analysis only.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s CMD ["dotnet", "ANalyze.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "ANalyze.Web.dll"]
