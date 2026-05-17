# Stage 0: build React UI
FROM node:20-alpine AS ui-build
WORKDIR /ui
COPY ui/package*.json ./
RUN npm ci
COPY ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Restore in a separate layer — only re-runs when .csproj changes
COPY src/HookVault/HookVault.csproj src/HookVault/
RUN dotnet restore src/HookVault/HookVault.csproj

COPY . .
# Copy built React UI into wwwroot so dotnet publish includes it
COPY --from=ui-build /ui/dist ./src/HookVault/wwwroot/
RUN dotnet publish src/HookVault/HookVault.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app

# /data is the SQLite volume mount point — must be owned by app before USER switch
RUN mkdir -p /data && chown app:app /data

COPY --from=build /app/publish .

USER app
EXPOSE 8080

# BusyBox wget ships in the alpine base image; no extra install needed.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "HookVault.dll"]
