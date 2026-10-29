# syntax=docker/dockerfile:1

# JADE stand-in image: the backend of this run, standing in for the ErewhonREST (65000) and
# WebServiceHTTP (21001) JADE applications, plus the internal admin listener (9090).
# Build from the repository root:  docker build -f standin/Dockerfile .
# The root .dockerignore admits only the paths copied below.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# This layout reproduces the repository's relative paths: the project's
# ../../../contracts/src/Erewhon.Contracts reference and the contracts project's embedded
# ../../operation-catalogue.json resolve only when contracts/ and standin/ sit side by side under
# /src beside the root build props.
COPY Directory.Packages.props Erewhon.Strangler.props /src/
COPY contracts/ /src/contracts/
COPY standin/ /src/standin/

# dotnet finds global.json by searching upward from the working directory, so the standin/global.json
# pins (SDK 10.0.403, rollForward disable) apply only to commands run from here.
WORKDIR /src/standin

# Locked mode fails on any drift from the committed packages.lock.json files of the stand-in and
# the contracts library.
RUN dotnet restore --locked-mode src/Erewhon.StandIn/Erewhon.StandIn.csproj

# Framework-dependent and RID-less: the app runs on the pinned shared framework 10.0.14
# (RollForward Disable), and a runtime identifier would not match the RID-less locked restore.
RUN dotnet publish src/Erewhon.StandIn/Erewhon.StandIn.csproj -c Release --no-restore \
    -o /app/publish -p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS runtime
WORKDIR /app

# A plain COPY keeps each file's build-context mode, and a working tree can hold 0600 files that
# the non-root user below could not read. a=rX makes every file read-only for all users and leaves
# directories traversable; names and bytes are unchanged.
#
# The seed directory is copied whole and unaltered (all 118 files, flat): /admin/info reports
# SeedFingerprint over it, and the harness requires that value to equal the fingerprint of the
# repository's Erewhon/DataFiles and of baseline/manifest.json, so any added, renamed or converted
# file is a reset mismatch. It precedes the application copy because it changes far less often.
COPY --chmod=a=rX Erewhon/DataFiles/ /app/seed/
COPY --from=build --chmod=a=rX /app/publish/ /app/

# appsettings.json defaults StandIn:SeedDirectory to the local-process path ../Erewhon/DataFiles.
# StandIn:ReferenceDate is left at its appsettings.json value, which the harness expects.
# Program.cs binds its three listeners explicitly from StandIn:*, so the image's default
# ASPNETCORE_HTTP_PORTS=8080 is cleared rather than overridden with a warning at start-up.
# No culture, globalization or time-zone variable is set: the baseline is captured from a local
# process and replayed against this container, so the image must add no behaviour of its own.
ENV StandIn__SeedDirectory=/app/seed \
    ASPNETCORE_HTTP_PORTS=

# REST (ErewhonREST 0.0.0.0:65000), SOAP (WebServiceHTTP 0.0.0.0:21001), admin (AdminRoutes.DefaultPort).
# Documentation only: Compose publishes none of them and runs the stand-in on the internal network.
EXPOSE 65000 21001 9090

# Every port is above 1024, the seed is only read and state is held in memory.
USER $APP_UID

# No HEALTHCHECK: the image has no curl, and the harness polls GET /health on the admin port.
# Exec form keeps dotnet as PID 1, so it receives SIGTERM directly and the container stops when
# POST /admin/shutdown stops the host.
ENTRYPOINT ["dotnet", "Erewhon.StandIn.dll"]
