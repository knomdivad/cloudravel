# Multi-stage build for the .NET 8 isolated-worker Azure Functions API.
# Build context is src/backend/ (see docker-compose.yml).

# Build natively on whatever host arch (dotnet/sdk has arm64 + amd64) — a
# `dotnet publish` without -r is portable IL, so the build stage's arch
# doesn't need to match the runtime stage's.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first for better layer caching
COPY AzureInventoryMonitor.Core/*.csproj AzureInventoryMonitor.Core/
COPY AzureInventoryMonitor.Infrastructure/*.csproj AzureInventoryMonitor.Infrastructure/
COPY AzureInventoryMonitor.Api/*.csproj AzureInventoryMonitor.Api/
RUN dotnet restore AzureInventoryMonitor.Api/AzureInventoryMonitor.Api.csproj

COPY . .
RUN dotnet publish AzureInventoryMonitor.Api/AzureInventoryMonitor.Api.csproj -c Release -o /app/publish --no-restore

# The official Azure Functions dotnet-isolated image only ships amd64 (no
# arm64 manifest as of this writing) — force it so this also builds on
# Apple Silicon/OrbStack. Runs under emulation there; native on amd64 hosts.
FROM --platform=linux/amd64 mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0
COPY --from=build /app/publish /home/site/wwwroot
