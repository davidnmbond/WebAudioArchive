FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/WebAudioArchive.Worker -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV WAA_CONFIG=/config/stations.json \
    WAA_DATA=/data \
    WAA_PORT=8080
VOLUME /data
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD ["dotnet", "WebAudioArchive.Worker.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "WebAudioArchive.Worker.dll"]
