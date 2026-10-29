# syntax=docker/dockerfile:1

# Equivalence harness image: the console app Erewhon.Harness (modes capture | replay | export-openapi |
# summary). Compose runs it as "harness" (replay) and "run-complete" (summary) from this one build.
# Build from the repository root:  docker build -f harness/Dockerfile .
# The root .dockerignore admits only the paths copied below.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# This layout reproduces the repository's relative paths: the project's
# ../../../contracts/src/Erewhon.Contracts reference and the contracts project's embedded
# ../../operation-catalogue.json resolve only when contracts/ and harness/ sit side by side under
# /src beside the root build props. harness/config and harness/tests come along but are not
# published: the harness reads its configuration from the bind-mounted repository at run time, and
# its unit tests run in the build step outside Compose.
COPY Directory.Packages.props Erewhon.Strangler.props /src/
COPY contracts/ /src/contracts/
COPY harness/ /src/harness/

# dotnet finds global.json by searching upward from the working directory, so the harness/global.json
# pins (SDK 10.0.403, rollForward disable) apply only to commands run from here.
WORKDIR /src/harness

# Locked mode fails on any drift from the committed packages.lock.json files of the harness and
# the contracts library.
RUN dotnet restore --locked-mode src/Erewhon.Harness/Erewhon.Harness.csproj

# Framework-dependent and RID-less: the app runs on the pinned shared framework 10.0.14
# (RollForward Disable), and a runtime identifier would not match the RID-less locked restore.
# No apphost, because the entry point below starts the assembly through dotnet.
RUN dotnet publish src/Erewhon.Harness/Erewhon.Harness.csproj -c Release --no-restore \
    -o /app/publish -p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS runtime

# git backs the JADE-unchanged and ordering checks, which read the bind-mounted repository's .git.
# Ubuntu noble's package is deliberately not version-pinned; nothing else uses it.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish/ /app/

# Nothing from the repository is baked in: baseline/, harness/config/, contracts/, Erewhon/DataFiles/
# and .git are read, and RESULTS.md and artifacts/ written, through the .:/repo bind mount
# (Harness:RepoRoot, default /repo). Compose supplies every Harness__* setting.
#
# The image's default root user is kept: the harness writes into a bind mount whose host ownership
# is arbitrary (which is also why its git calls pass -c safe.directory), and the report-write
# failure check assumes a root process that is refused only because the path is a directory.
#
# No EXPOSE and no HEALTHCHECK: the harness has no listener and is itself the readiness poller.
# No CMD: Compose supplies the mode ("replay" or "summary"). Exec form keeps dotnet as PID 1, so a
# container stop delivers SIGTERM to the harness, which cancels the mode and still runs replay's
# report and shutdown steps.
ENTRYPOINT ["dotnet", "Erewhon.Harness.dll"]
