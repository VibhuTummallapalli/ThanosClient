# Minecraft Console Client, from the project's own self-contained linux-x64 release.
# Building from source would pull the whole .NET SDK into the image; this does not.
FROM debian:12-slim

# Pinned deliberately. MCC's config format shifts between releases, so an unpinned
# image would silently rewrite MinecraftClient.ini on the next rebuild.
ARG MCC_VERSION=20260827-508

# libicu: the release is not built with invariant globalization, so it needs real ICU.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates libicu72 \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

ADD --chmod=0755 \
    https://github.com/MCCTeam/Minecraft-Console-Client/releases/download/${MCC_VERSION}/MinecraftClient-${MCC_VERSION}-linux-x64 \
    /usr/local/bin/mcc

# uid 1000 so a bind-mounted data dir owned by the host's first user is writable
# without a chown on every deploy.
RUN useradd --uid 1000 --create-home mcc
USER mcc

# Everything that must survive a rebuild lives here: MinecraftClient.ini, the session
# cache, and the scripts. Mount a volume over it.
WORKDIR /opt/data

ENTRYPOINT ["/usr/local/bin/mcc"]
