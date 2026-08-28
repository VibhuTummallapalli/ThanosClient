# Build stage: restore against the csproj alone so the dependency layer caches across
# source edits.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src
COPY src/ThanosClient/ThanosClient.csproj src/ThanosClient/
RUN dotnet restore src/ThanosClient/ThanosClient.csproj

COPY src/ src/
# PublishSingleFile is off here: it needs a RuntimeIdentifier, and a framework-dependent
# publish is what the runtime image expects anyway.
RUN dotnet publish src/ThanosClient/ThanosClient.csproj \
    -c Release -o /app --no-restore -p:PublishSingleFile=false

# Runtime stage: a console client needs the base runtime, not ASP.NET.
FROM mcr.microsoft.com/dotnet/runtime:9.0

COPY --from=build /app /app

# /data holds everything that must survive a redeploy: the config, the cached session,
# and the chat log. Mount a volume over it.
RUN useradd --create-home --uid 1000 thanos \
 && mkdir -p /data \
 && chown -R thanos:thanos /data

USER thanos
WORKDIR /data

# Running detached leaves no terminal attached, which the client handles by not reading
# console input.
ENTRYPOINT ["dotnet", "/app/ThanosClient.dll"]
