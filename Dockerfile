# Braggarr: one container, one volume (/config), one port (9494). The braggarr binary, Native AOT, serving.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props braggarr.slnx ./
COPY src/ src/
RUN dotnet publish src/Braggarr.Cli -c Release --use-current-runtime -o /app -p:MinVerSkip=true

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
# git for the posts repo clone; tzdata so the calendar can show the owner's time zone.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git tzdata ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/braggarr .
ENV ASPNETCORE_URLS=http://+:9494 \
    BRAGGARR_CONFIG=/config \
    HOME=/config \
    DOTNET_gcServer=0
VOLUME /config
EXPOSE 9494
# The image's unprivileged user. Compose may run it as another uid (PUID:PGID) that owns /config.
USER app
HEALTHCHECK --interval=1m --timeout=5s CMD ["/app/braggarr", "healthcheck"]
ENTRYPOINT ["/app/braggarr", "serve"]
