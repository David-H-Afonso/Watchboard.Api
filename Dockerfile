# Compile natively; portable output includes SQLite's target-specific native assets.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Watchboard.Api.csproj packages.lock.json ./
RUN dotnet restore Watchboard.Api.csproj --locked-mode
COPY . .
RUN dotnet publish Watchboard.Api.csproj --configuration Release --no-restore \
    --no-self-contained -p:UseAppHost=false --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/storage \
    && chown "$APP_UID:$APP_UID" /app/storage \
    && chmod 700 /app/storage
COPY --from=build /out/ ./
COPY LICENSE /usr/share/licenses/watchboard/LICENSE
ENV URLS=http://+:8080 \
    DOTNET_ENVIRONMENT=Production \
    Watchboard__DatabasePath=/app/storage/watchboard.db
LABEL org.opencontainers.image.title="Watchboard API" \
    org.opencontainers.image.version="1.0.0" \
    org.opencontainers.image.source="https://github.com/David-H-Afonso/Watchboard.Api" \
    org.opencontainers.image.licenses="MIT"
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --output /dev/null http://127.0.0.1:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "Watchboard.Api.dll"]
