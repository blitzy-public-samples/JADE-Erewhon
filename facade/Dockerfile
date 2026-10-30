# syntax=docker/dockerfile:1

# Strangler facade images, built from the repository root:
#   docker build -f facade/Dockerfile --target runtime .   Compose service "facade": owns the
#       ErewhonREST (65000) and WebServiceHTTP (21001) addresses and forwards every call to its
#       routed target, plus the internal admin listener (9090)
#   docker build -f facade/Dockerfile --target test .      Compose service "facade-tests": runs the
#       routing, failure-behaviour and listener-isolation tests and writes their TRX
# The root .dockerignore admits only the paths copied below.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# The pinned SDK image ships the native libbrotli1 1.1.0-2build2, which USN-8956-1 (CVE-2026-111497)
# fixes in 1.1.0-2ubuntu0.1. Only that package is upgraded, so the SDK and every other package stay
# as pinned, and the test stage inherits the upgrade. The version check fails the build rather than
# ship a vulnerable libbrotli1, and the apt lists are removed in the same layer.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends --only-upgrade libbrotli1 \
    && dpkg --compare-versions "$(dpkg-query -W -f='${Version}' libbrotli1)" ge 1.1.0-2ubuntu0.1 \
    && rm -rf /var/lib/apt/lists/*

# This layout reproduces the repository's relative paths: the facade's
# ../../../contracts/src/Erewhon.Contracts reference and the contracts project's embedded
# ../../operation-catalogue.json resolve only when contracts/ and facade/ sit side by side under
# /src beside the root build props.
COPY Directory.Packages.props Erewhon.Strangler.props /src/
COPY contracts/ /src/contracts/
COPY facade/ /src/facade/

# dotnet finds global.json by searching upward from the working directory, so the facade/global.json
# pins (SDK 10.0.403, rollForward disable, Microsoft.Testing.Platform runner) apply only to commands
# run from here.
WORKDIR /src/facade

# Locked mode rejects a restore whose dependencies or requested versions disagree with the committed
# packages.lock.json files of the facade, its tests and the contracts library. The test project is
# restored here as well, because the test stage runs under network_mode none and can never restore.
RUN dotnet restore --locked-mode src/Erewhon.Facade/Erewhon.Facade.csproj
RUN dotnet restore --locked-mode tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj

# Framework-dependent and RID-less: the app runs on the pinned shared framework 10.0.14
# (RollForward Disable), and a runtime identifier would not match the RID-less locked restore.
# No apphost, because the runtime entry point starts the assembly through dotnet.
RUN dotnet publish src/Erewhon.Facade/Erewhon.Facade.csproj -c Release --no-restore \
    -o /app/publish -p:UseAppHost=false


# Declared before runtime so that a build without --target yields the runtime image.
FROM build AS test
WORKDIR /src/facade

# The SDK image keeps the restored obj/ directories and the NuGet package cache of the build stage,
# so the container needs no network. Release matches the configuration tests/run-tests.sh passes to
# dotnet test, which then finds the build up to date.
RUN dotnet build tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj -c Release --no-restore

# The SDK image's root user is kept: the results go to /repo/artifacts/facade-tests in a bind mount
# whose host ownership is arbitrary, and run-tests.sh leaves them writable for later local runs.
# Running the script through bash does not depend on its executable bit surviving the checkout.
# The script always exits 0, so the harness's service_completed_successfully dependency holds; the
# test outcome travels in the TRX and exit-code files.
ENTRYPOINT ["/bin/bash", "/src/facade/tests/run-tests.sh"]


FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS runtime
WORKDIR /app

# A plain COPY keeps each file's build-context mode, and a working tree can hold 0600 files that
# the non-root user below could not read. a=rX makes every file read-only for all users and leaves
# directories traversable; names and bytes are unchanged.
#
# The whole contracts/ directory is copied, never contracts/wsdl/ alone: contracts/ always exists,
# so a capture that obtained no WSDL cannot fail this build. When present, the WSDL lands at
# /app/contracts/wsdl/ErewhonWebService.wsdl, the default Facade:WsdlPath resolved against the
# application base directory; when absent, the facade still starts, reports it absent on
# /admin/info and forwards WSDL requests to the default target unchanged.
COPY --chmod=a=rX contracts/ /app/contracts/
COPY --from=build --chmod=a=rX /app/publish/ /app/

# Program.cs binds its three listeners explicitly from Facade:RestPort, Facade:SoapPort and
# Facade:AdminPort on Facade:BindAddress (0.0.0.0), so the image's default ASPNETCORE_HTTP_PORTS=8080
# is cleared rather than overridden with a warning at start-up. No backend URL, routing override or
# timeout is set here: Compose supplies Routing__Targets__backend__RestBaseUrl,
# Routing__Targets__backend__SoapBaseUrl and Backend__TimeoutSeconds from .env.
ENV ASPNETCORE_HTTP_PORTS=

# Temporary storage of SOAP bodies. A SOAP request without a matching SOAPAction is buffered whole
# (Request.EnableBuffering, with no limit) so that its first 64 KiB can be inspected and the full body
# still forwarded; a body over 30 KiB is buffered in a file here. This location must hold the largest
# SOAP bodies sent concurrently without a matching SOAPAction: when it fills, the connection is
# aborted without a response and the call is logged as "failed" with status 0. For larger bodies,
# mount a volume of the needed size and point ASPNETCORE_TEMP at it. /tmp is the image's
# world-writable default, named here so the requirement is visible.
ENV ASPNETCORE_TEMP=/tmp

# REST (ErewhonREST 0.0.0.0:65000), SOAP (WebServiceHTTP 0.0.0.0:21001), admin (AdminRoutes.DefaultPort).
# Documentation only: Compose publishes 65000 and 21001 and never 9090.
EXPOSE 65000 21001 9090

# Every port is above 1024. The facade reads its WSDL and embedded catalogue, and writes only the
# temporary files of buffered SOAP bodies under ASPNETCORE_TEMP.
USER $APP_UID

# No HEALTHCHECK: the image has no curl, and the harness polls GET /health on the admin port.
# Exec form keeps dotnet as PID 1, so it receives SIGTERM directly and the container stops when
# POST /admin/shutdown stops the host. No CMD: the facade takes no arguments.
ENTRYPOINT ["dotnet", "Erewhon.Facade.dll"]
