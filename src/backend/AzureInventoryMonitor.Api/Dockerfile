# ============================================================================
# Azure Functions (.NET 8 isolated) — AIM backend API
# Build context: src/backend  (so the three projects resolve their references)
#   docker build -f AzureInventoryMonitor.Api/Dockerfile -t aim-api .
# ============================================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the csproj set first for layer caching
COPY AzureInventoryMonitor.Core/AzureInventoryMonitor.Core.csproj AzureInventoryMonitor.Core/
COPY AzureInventoryMonitor.Infrastructure/AzureInventoryMonitor.Infrastructure.csproj AzureInventoryMonitor.Infrastructure/
COPY AzureInventoryMonitor.Api/AzureInventoryMonitor.Api.csproj AzureInventoryMonitor.Api/
RUN dotnet restore AzureInventoryMonitor.Api/AzureInventoryMonitor.Api.csproj

# Build + publish
COPY . .
RUN dotnet publish AzureInventoryMonitor.Api/AzureInventoryMonitor.Api.csproj \
    -c Release -o /out --no-restore

# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    FUNCTIONS_WORKER_RUNTIME=dotnet-isolated
COPY --from=build /out /home/site/wwwroot
# The base image entrypoint starts the Functions host on port 80.
