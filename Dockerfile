# syntax=docker/dockerfile:1

# --- Stage 1: build the React SPA ---------------------------------------------
FROM node:22-alpine AS web
WORKDIR /src
# Shared static assets (favicon/logo) live in the repo-root public/ — Vite's publicDir
# points here (../public relative to web/), so it must be present at build time.
COPY public/ ./public/
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --legacy-peer-deps
COPY web/ ./
RUN npm run build            # outputs /src/web/dist

# --- Stage 2: build & publish the ASP.NET Core API ----------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY core/calendarITCore/ ./
# Restore/publish the host project directly (not the .slnx) — restoring the solution trips a
# GA-SDK bug ("'latest' is not a valid version string") and pulls in the unneeded test project.
RUN dotnet restore calendarITCore/calendarITCore.csproj
RUN dotnet publish calendarITCore/calendarITCore.csproj -c Release -o /app/publish --no-restore

# --- Stage 3: runtime ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=api /app/publish ./
# The API serves the SPA from wwwroot.
COPY --from=web /src/web/dist ./wwwroot

# Container serves plain HTTP; the operator's reverse proxy terminates TLS.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Basic container healthcheck against the liveness endpoint.
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "calendarITCore.dll"]
