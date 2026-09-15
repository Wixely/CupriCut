# syntax=docker/dockerfile:1.7
#
# The one departure from house style, and the reason it exists: this image carries ffmpeg.
#
# The release zips do not, because a renderer that cannot find ffmpeg is a working renderer with
# render_video disabled, and bundling a media encoder into a NuGet-shaped release is somebody
# else's licensing problem. In a container it is the opposite: an image whose whole job is turning
# HTML into MP4 and which then cannot encode one is a broken image. Cut:FfmpegPath names it either
# way, and probe says whether it answered.
#
# What is NOT here is a browser. That is the point of the project.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY CupriCut.csproj ./
COPY cli/CupriCut.Cli.csproj ./cli/

ARG TARGETARCH

# GitHub Packages will not serve even a public package anonymously, so this restore needs a token.
# It arrives as a BuildKit SECRET rather than an ARG, so it never lands in an image layer or in
# `docker history`:
#
#   docker build --secret id=NUGET_AUTH_TOKEN,env=GITHUB_TOKEN -t cupricut .
#
# The username is a placeholder because GitHub Packages authenticates on the token alone; any
# non-empty value is accepted.
ARG NUGET_AUTH_USER=docker
RUN --mount=type=secret,id=NUGET_AUTH_TOKEN,required=true \
    arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet nuget update source GitHub-Wixely-Packages \
      --username "$NUGET_AUTH_USER" \
      --password "$(cat /run/secrets/NUGET_AUTH_TOKEN)" \
      --store-password-in-clear-text && \
    dotnet restore CupriCut.csproj -r "$rid" -p:PublishSingleFile=true -p:SelfContained=false -p:EnableCompressionInSingleFile=false && \
    dotnet restore cli/CupriCut.Cli.csproj -r "$rid" -p:PublishSingleFile=true -p:SelfContained=false -p:EnableCompressionInSingleFile=false

COPY . .

# CupriCutSelfContained is passed as well as the self-contained switch, and it is not redundant.
# cli references CupriCut.csproj, and two executables where one references the other must AGREE
# about self-containment or the SDK refuses with NETSDK1151. Once a RuntimeIdentifier is present
# the referenced exe resolves to self-contained whatever the publish was told, and neither the
# switch nor a global SelfContained property crosses the ProjectReference. A custom property does.
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    for proj in CupriCut.csproj cli/CupriCut.Cli.csproj; do \
      dotnet publish "$proj" \
        -c Release \
        --no-restore \
        -r "$rid" \
        --self-contained false \
        -p:CupriCutSelfContained=false \
        -o /app/publish \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=false \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:IncludeAllContentForSelfExtract=true \
        -p:IsTransformWebConfigDisabled=true \
        -p:StaticWebAssetsEnabled=false \
        -p:DebugType=none \
        -p:DebugSymbols=false; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# ffmpeg, and the fontconfig/freetype pieces Skia's text rasteriser needs on a slim image. The
# font policy is RegisteredOnly so no SYSTEM font is ever used for a render - these are here for
# Skia's own initialisation, not to give a composition something to fall back to.
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg libfontconfig1 libfreetype6 && \
    rm -rf /var/lib/apt/lists/*

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    CUPRICUT_Server__Host=0.0.0.0 \
    CUPRICUT_Server__Port=5722 \
    CUPRICUT_Server__Path=/mcp \
    CUPRICUT_Server__Password= \
    CUPRICUT_Cut__FfmpegPath=/usr/bin/ffmpeg \
    CUPRICUT_Cut__EnableVideo=true

RUN mkdir -p /app/logs /app/output /app/projects /app/compositions && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

# The command is cupricut even though the assembly is CupriCut.Cli - NuGet refuses two assemblies
# in one solution whose names differ only by case, and the server has to stay CupriCut.
RUN printf '#!/bin/sh\nexec /app/CupriCut.Cli "$@"\n' > /usr/local/bin/cupricut && \
    chmod +x /usr/local/bin/cupricut

USER $APP_UID
EXPOSE 5722

# output and projects are the two directories the server writes to; compositions is what it reads.
VOLUME ["/app/logs", "/app/output", "/app/projects", "/app/compositions"]

# -c explicitly, even though DOTNET_RUNNING_IN_CONTAINER above already implies it: a container has
# no display, and "why did it try to open a window" is a worse thing to debug than a redundant flag.
ENTRYPOINT ["./CupriCut", "-c"]
