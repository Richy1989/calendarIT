# syntax=docker/dockerfile:1
#
# CalendarIT — one Dockerfile, two images (build context: the repo root).
#
#   docker build .                   → "runtime" (default): the .NET app on :8080 serving the API
#                                      and the SPA itself. What docker-compose.yml builds.
#   docker build --target bundle .   → "bundle": nginx on :80 in front of the app, all state under
#                                      /data. The published image (Docker Hub, Unraid templates).
#
# Both start as root only long enough to make the data directory theirs, then run the .NET app
# as an unprivileged user (PUID/PGID, default 1654 — the base image's "app" user). Existing
# volumes written by older root-run images are taken over automatically on start.

# ---- 1. Build the React SPA (the committed OpenAPI client means no backend is needed here) ----
FROM node:22-alpine AS web
WORKDIR /src
# Static assets (favicon/logo) live in the repo-root public/ — Vite's publicDir is ../public.
COPY public/ ./public/
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --legacy-peer-deps
COPY web/ ./
RUN npm run build

# ---- 2. Publish the .NET API ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
# Release version (git tag, e.g. 0.2.0) and commit sha, injected by the build script. A local
# `docker build` without args produces an honest "0.0.0-dev".
ARG VERSION=0.0.0-dev
ARG COMMIT=
WORKDIR /src
COPY core/calendarITCore/ ./
# Restore/publish the host project directly (not the .slnx). Restoring the solution trips a
# GA-SDK bug ("'latest' is not a valid version string") and would also pull in the test project.
RUN dotnet restore calendarITCore/calendarITCore.csproj
RUN INFOVERSION="$VERSION"; [ -n "$COMMIT" ] && INFOVERSION="$VERSION+$COMMIT"; \
    dotnet publish calendarITCore/calendarITCore.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false \
      /p:Version="$VERSION" /p:InformationalVersion="$INFOVERSION"

# ---- 3a. Bundle: nginx + API in one container (published image, Unraid) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS bundle
RUN apt-get update \
 && apt-get install -y --no-install-recommends nginx \
 && rm -rf /var/lib/apt/lists/* /etc/nginx/sites-enabled/default

WORKDIR /app
COPY --from=api /app/publish ./
COPY --from=web /src/web/dist /usr/share/nginx/html
COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY deploy/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# The API listens on :8080 (nginx proxies to it); nginx is the public face on :80.
# All writable data lives under /data (SQLite db, keys, uploads). JWT_SIGNING_KEY is auto-generated
# and persisted under /data by the entrypoint unless you provide one.
#
# FORWARDED_PROXY_HOPS=2 because this image has two proxies in front of the API: your reverse
# proxy, then the nginx inside the container. Both append to X-Forwarded-For, and trusting both
# is what makes the client IP the auth rate limit keys on a real one. Set it to 1 if nothing
# sits in front of the container.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    APPDATA_PATH=/data \
    DATABASE_PROVIDER=Sqlite \
    APPLY_MIGRATIONS=true \
    FORWARDED_PROXY_HOPS=2

EXPOSE 80
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/80 && printf 'GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["/entrypoint.sh"]

# ---- 3b. Runtime (default): the .NET app serves API + SPA on :8080 (docker-compose.yml) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=api /app/publish ./
# The API serves the SPA from wwwroot.
COPY --from=web /src/web/dist ./wwwroot
COPY deploy/entrypoint-api.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Container serves plain HTTP; the operator's reverse proxy terminates TLS.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# No curl/wget in the base image; bash's /dev/tcp is enough for a liveness probe.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["/entrypoint.sh"]
