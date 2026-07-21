# syntax=docker/dockerfile:1
#
# Single-container build of CalendarIT for simple, self-contained deployments (e.g. Unraid).
# It bundles three things into one image:
#   1. the built React SPA, served by nginx
#   2. the .NET API (calendarITCore), listening on :8080 inside the container
#   3. nginx as the public face on :80, serving the SPA and reverse-proxying /api to the API
# React is NOT hosted by ASP.NET here — nginx and the API are separate processes talking over
# HTTP, just co-located. With no Postgres configured the API uses its SQLite fallback, and ALL
# writable state (SQLite db, uploaded avatars) lives under /data — mount that to persist it.
#
# Build context is the repo root:  docker build -f deploy/Dockerfile -t richy1989/calendarit:latest .

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

# ---- 3. Runtime: API + nginx in one image ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
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
# All writable data lives under /data (SQLite db + uploads). JWT_SIGNING_KEY is auto-generated
# and persisted under /data by the entrypoint unless you provide one.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    APPDATA_PATH=/data \
    DATABASE_PROVIDER=Sqlite \
    APPLY_MIGRATIONS=true

EXPOSE 80
VOLUME ["/data"]
ENTRYPOINT ["/entrypoint.sh"]
