# syntax=docker/dockerfile:1
# Multi-stage build for the .NET 8 isolated-worker Azure Functions API.
# Build context is src/backend/ (see docker-compose.yml).

# Build natively on whatever host arch (dotnet/sdk has arm64 + amd64) — a
# `dotnet publish` without -r is portable IL, so the build stage's arch
# doesn't need to match the runtime stage's.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Optional short git SHA, passed by docker-compose.yml from the host — the build
# context (src/backend/) has no .git dir, so the SDK's own git auto-detection
# can't find one; this is how a Docker build still stamps a commit into the
# version shown in the UI. Empty is fine (falls back to a bare semver).
ARG GIT_SHA=""

# Some networks resolve NuGet's hosts to an IPv6 address that silently
# blackholes (connects nowhere, times out ~20-75s) instead of falling back to
# IPv4 quickly — skipping IPv6 entirely sidesteps that regardless of cause.
ENV DOTNET_SYSTEM_NET_DISABLEIPV6=1

# Restore first for better layer caching. Cache mount: NuGet packages persist
# across builds even when a .csproj change invalidates the layer above, so
# that only costs re-resolving the dependency graph, not re-downloading it.
COPY CloudRavel.Core/*.csproj CloudRavel.Core/
COPY CloudRavel.Infrastructure/*.csproj CloudRavel.Infrastructure/
COPY CloudRavel.Api/*.csproj CloudRavel.Api/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore CloudRavel.Api/CloudRavel.Api.csproj

COPY . .
# --no-restore skips re-resolving, but publish still reads the actual package
# files from the global cache on disk — needs the same mount as restore above,
# since a cache mount is only attached to the RUN instruction that declares it.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish CloudRavel.Api/CloudRavel.Api.csproj -c Release -o /app/publish --no-restore \
    ${GIT_SHA:+-p:SourceRevisionId=$GIT_SHA}

# The official Azure Functions dotnet-isolated image only ships amd64 (no
# arm64 manifest as of this writing) — force it so this also builds on
# Apple Silicon/OrbStack. Runs under emulation there; native on amd64 hosts.
FROM --platform=linux/amd64 mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0
COPY --from=build /app/publish /home/site/wwwroot
