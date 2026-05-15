FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Restore in a separate layer — only re-runs when .csproj changes
COPY src/HookVault/HookVault.csproj src/HookVault/
RUN dotnet restore src/HookVault/HookVault.csproj

COPY . .
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
ENTRYPOINT ["dotnet", "HookVault.dll"]
