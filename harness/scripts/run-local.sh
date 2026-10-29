#!/usr/bin/env bash
# harness/scripts/run-local.sh - Docker-less local-process runner for the Erewhon strangler facade.
#
# usage: harness/scripts/run-local.sh capture|export-openapi|replay
#
#   capture         Builds the stand-in and the harness, starts the stand-in and runs `harness capture`,
#                   which writes baseline/** and, when it is absent, contracts/wsdl/ErewhonWebService.wsdl.
#                   It reads, builds and starts nothing under facade/, so it runs before the facade exists.
#   export-openapi  Builds the facade and the harness, starts the facade alone (the OpenAPI document needs
#                   no backend) and runs `harness export-openapi`, which writes
#                   contracts/openapi/erewhon-rest.openapi.json. Run once, after the facade commit.
#   replay          The local fallback for `docker compose up --build` (KL-SUB-DOCKER). Builds the three
#                   components and the facade tests, runs the facade tests into artifacts/facade-tests/,
#                   starts the stand-in and the facade, and runs `harness replay`, which writes RESULTS.md
#                   and artifacts/**, shuts both down through /admin/shutdown and prints the final line.
#
# Port layout. Every listener of both services binds 127.0.0.1 only, so neither unauthenticated admin
# port is reachable from another host:
#   stand-in  REST 15000  SOAP 15001  admin 15090
#   facade    REST 65000  SOAP 21001  admin  9090
#
# Environment overrides honoured:
#   BACKEND_REST_URL, BACKEND_SOAP_URL  Where the facade forwards; default the local stand-in.
#                         BACKEND_REST_URL=http://127.0.0.1:1 is the local forced-502 exit-code check.
#   BACKEND_TIMEOUT_SECONDS  The facade's Backend:TimeoutSeconds; default 30.
#   BACKEND_ADMIN_URL     The harness's backend admin URL; default the local stand-in. Set it explicitly
#                         empty for an external backend: no reset and no stand-in shutdown (KL-SUB-NORESET).
#   HARNESS_REPORT_PATH   Where replay writes the report; default <repository>/RESULTS.md.
#   TFM                   Target-framework folder of the build output; default net10.0.
#
# The script never reads .env: its values are Compose service hostnames (http://standin:65000), which
# local processes cannot resolve. It adds no credential, token or authentication header anywhere.
#
# Every dotnet command runs from inside standin/, facade/ or harness/, so that component's global.json
# (SDK pin, Microsoft.Testing.Platform runner) applies; none runs from the repository root or contracts/.
# Logs go to artifacts/local/.
#
# Exit status: the harness's own, 0 or 1. Usage errors, build failures and interrupts exit 1. Every
# process the script starts is stopped by its pid on every exit path.

set -uo pipefail

# Associative arrays and waiting on a process substitution's pid need bash 4.4.
if [ -z "${BASH_VERSINFO:-}" ] ||
    ((BASH_VERSINFO[0] < 4 || (BASH_VERSINFO[0] == 4 && BASH_VERSINFO[1] < 4))); then
    echo "run-local.sh: bash 4.4 or later is required" >&2
    exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ART="$REPO_ROOT/artifacts"
LOGDIR="$ART/local"
TFM="${TFM:-net10.0}"
readonly REPO_ROOT ART LOGDIR TFM
readonly CONFIGURATION=Release

readonly LOOPBACK=127.0.0.1
readonly STANDIN_REST_PORT=15000 STANDIN_SOAP_PORT=15001 STANDIN_ADMIN_PORT=15090
readonly FACADE_REST_PORT=65000 FACADE_SOAP_PORT=21001 FACADE_ADMIN_PORT=9090

readonly STANDIN_DLL="src/Erewhon.StandIn/bin/$CONFIGURATION/$TFM/Erewhon.StandIn.dll"
readonly FACADE_DLL="src/Erewhon.Facade/bin/$CONFIGURATION/$TFM/Erewhon.Facade.dll"
readonly HARNESS_DLL="src/Erewhon.Harness/bin/$CONFIGURATION/$TFM/Erewhon.Harness.dll"

# Grace before SIGKILL for the stand-in and the facade, and for the harness when the run is interrupted.
# The harness bound covers its own exit path: report, then up to 15 s of shutdown per component.
readonly STOP_SECONDS=10
readonly HARNESS_STOP_SECONDS=45

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# No persistent MSBuild node, MSBuild server or compiler server outlives a build, so the script leaves
# no process running that it did not stop. A value the caller set is kept.
export MSBUILDDISABLENODEREUSE="${MSBUILDDISABLENODEREUSE:-1}"
export DOTNET_CLI_USE_MSBUILD_SERVER="${DOTNET_CLI_USE_MSBUILD_SERVER:-0}"
export UseSharedCompilation="${UseSharedCompilation:-false}"

PIDS=()
declare -A NAMES=()
PROC_ENV=()
HARNESS_PID=""
TEE_PID=""
CLEANED=0

usage() {
    echo "usage: harness/scripts/run-local.sh capture|export-openapi|replay" >&2
}

# Prints a repository path relative to the repository root.
rel() {
    printf '%s' "${1#"$REPO_ROOT"/}"
}

# Prints the last <count> lines of <file> to stderr, each quoted with "> ".
quote_tail() {
    local count="$1" file="$2"
    if [ -f "$file" ]; then
        tail -n "$count" "$file" | sed 's/^/> /' >&2
    fi
}

# --- Process environments -------------------------------------------------------------------------
# Each function sets PROC_ENV to the NAME=value settings of one process. start_bg and run_harness hand
# them to that process alone through env(1), so the settings of one component never reach another. The
# keys are the ones docker-compose.yml sets, with local values.

standin_env() {
    # StandIn:ReferenceDate keeps its 2026-01-01 default, which must equal Harness:ExpectedReferenceDate.
    PROC_ENV=(
        "StandIn__SeedDirectory=$REPO_ROOT/Erewhon/DataFiles"
        "StandIn__BindAddress=$LOOPBACK"
        "StandIn__RestPort=$STANDIN_REST_PORT"
        "StandIn__SoapPort=$STANDIN_SOAP_PORT"
        "StandIn__AdminPort=$STANDIN_ADMIN_PORT"
    )
}

facade_env() {
    # The facade still starts when the WSDL file is absent and reports `wsdl: absent` in /admin/info.
    PROC_ENV=(
        "Facade__BindAddress=$LOOPBACK"
        "Facade__RestPort=$FACADE_REST_PORT"
        "Facade__SoapPort=$FACADE_SOAP_PORT"
        "Facade__AdminPort=$FACADE_ADMIN_PORT"
        "Facade__WsdlPath=$REPO_ROOT/contracts/wsdl/ErewhonWebService.wsdl"
        "Routing__Targets__backend__RestBaseUrl=${BACKEND_REST_URL:-http://$LOOPBACK:$STANDIN_REST_PORT}"
        "Routing__Targets__backend__SoapBaseUrl=${BACKEND_SOAP_URL:-http://$LOOPBACK:$STANDIN_SOAP_PORT}"
        "Backend__TimeoutSeconds=${BACKEND_TIMEOUT_SECONDS:-30}"
    )
}

harness_env() {
    # The backend REST and SOAP URLs name the local stand-in even when BACKEND_REST_URL or
    # BACKEND_SOAP_URL redirect the facade, so reset and the WSDL fetch still reach the stand-in during
    # the forced-502 check. The admin URL uses `-`, not `:-`: an explicitly empty BACKEND_ADMIN_URL stays
    # empty and marks an external backend.
    PROC_ENV=(
        "Harness__RepoRoot=$REPO_ROOT"
        "Harness__ExecutionMode=local-processes"
        "Harness__ReportPath=${HARNESS_REPORT_PATH:-$REPO_ROOT/RESULTS.md}"
        "Harness__Target__RestBaseUrl=http://$LOOPBACK:$FACADE_REST_PORT"
        "Harness__Target__SoapBaseUrl=http://$LOOPBACK:$FACADE_SOAP_PORT"
        "Harness__Target__AdminUrl=http://$LOOPBACK:$FACADE_ADMIN_PORT"
        "Harness__Backend__RestBaseUrl=http://$LOOPBACK:$STANDIN_REST_PORT"
        "Harness__Backend__SoapBaseUrl=http://$LOOPBACK:$STANDIN_SOAP_PORT"
        "Harness__Backend__AdminUrl=${BACKEND_ADMIN_URL-http://$LOOPBACK:$STANDIN_ADMIN_PORT}"
    )
}

# --- Helpers --------------------------------------------------------------------------------------

# build <component> <project path relative to the component> [extra dotnet build arguments]
# Restore is implicit and deliberately not locked: the first local build writes the committed
# packages.lock.json files, and the Dockerfiles enforce locked mode.
build() {
    local component="$1" project="$2" log status
    shift 2
    log="$LOGDIR/build-$component-$(basename "$project" .csproj).log"
    echo "run-local.sh: building $component/$project ($CONFIGURATION, log $(rel "$log"))"
    (cd "$REPO_ROOT/$component" && dotnet build "$project" -c "$CONFIGURATION" "$@") >"$log" 2>&1
    status=$?
    if [ "$status" -ne 0 ]; then
        echo "run-local.sh: build failed: $component $project" >&2
        quote_tail 40 "$log"
        exit 1
    fi
}

# port_busy <port>: true when something already accepts connections on 127.0.0.1:<port>. The /dev/tcp
# path is bash's own connect syntax; nothing under /dev is opened or touched.
port_busy() {
    # shellcheck disable=SC2016 # $0 and $1 are expanded by the inner bash, from its arguments.
    timeout 2 bash -c 'exec 3<>"/dev/tcp/$0/$1"' "$LOOPBACK" "$1" 2>/dev/null
}

# warn_busy <name> <port>...: warns about ports a leftover process already holds; the run continues.
warn_busy() {
    local name="$1" port
    shift
    for port in "$@"; do
        if port_busy "$port"; then
            echo "run-local.sh: warning: $LOOPBACK:$port is already in use; $name may fail to start" >&2
        fi
    done
}

# start_bg <name> <component> <dll path relative to the component> [application arguments]
# Starts the process with the settings the caller just put in PROC_ENV. exec (and env's own exec) make
# the background subshell's pid the dotnet process itself, so $! is the pid to stop. The script does not
# wait for readiness: the harness polls each /health within its readiness limit.
start_bg() {
    local name="$1" component="$2" dll="$3" log pid
    shift 3
    log="$LOGDIR/$name.log"
    (
        cd "$REPO_ROOT/$component" || exit 1
        exec env "${PROC_ENV[@]}" dotnet "$dll" "$@"
    ) >"$log" 2>&1 &
    pid=$!
    PIDS+=("$pid")
    NAMES[$pid]="$name"
    echo "started $name pid $pid (log $(rel "$log"))"
    sleep 1
    if ! kill -0 "$pid" 2>/dev/null; then
        echo "run-local.sh: $name pid $pid exited during start-up; last lines of $(rel "$log"):" >&2
        quote_tail 20 "$log"
    fi
}

# stop_pid <pid> [grace seconds]: SIGTERM, then SIGKILL after the grace period, then reap. A pid that has
# already exited (the harness shuts the components down itself) is only reaped.
stop_pid() {
    local pid="$1" grace="${2:-$STOP_SECONDS}" name="${NAMES[$1]:-process}" ticks=0
    if kill -0 "$pid" 2>/dev/null; then
        kill -TERM "$pid" 2>/dev/null
        while kill -0 "$pid" 2>/dev/null && [ "$ticks" -lt $((grace * 2)) ]; do
            sleep 0.5
            ticks=$((ticks + 1))
        done
        if kill -0 "$pid" 2>/dev/null; then
            kill -KILL "$pid" 2>/dev/null
            ticks=0
            while kill -0 "$pid" 2>/dev/null && [ "$ticks" -lt 10 ]; do
                sleep 0.5
                ticks=$((ticks + 1))
            done
        fi
    fi
    if kill -0 "$pid" 2>/dev/null; then
        echo "run-local.sh: $name pid $pid still running" >&2
    else
        wait "$pid" 2>/dev/null
    fi
}

# cleanup: stops, once, the harness if it is still running and then every started process in reverse
# start order (the facade before the stand-in).
cleanup() {
    local i
    if [ "$CLEANED" -eq 1 ]; then
        return 0
    fi
    CLEANED=1
    if [ -n "$HARNESS_PID" ]; then
        stop_pid "$HARNESS_PID" "$HARNESS_STOP_SECONDS"
        HARNESS_PID=""
    fi
    for ((i = ${#PIDS[@]} - 1; i >= 0; i--)); do
        stop_pid "${PIDS[i]}"
    done
}

# shellcheck disable=SC2317 # Invoked only through the INT, TERM and HUP trap below.
on_signal() {
    trap '' INT TERM HUP
    echo "run-local.sh: interrupted; stopping every process this run started" >&2
    cleanup
    exit 1
}

trap cleanup EXIT
trap on_signal INT TERM HUP

# run_harness <mode>: runs the harness and sets `code` to its status, 0 or 1. Its output stays on the
# console, which report recovery depends on, and is also kept in artifacts/local/harness-<mode>.log.
# The harness runs as a background job that the script waits on, so a signal to the script interrupts
# the wait at once and cleanup passes SIGTERM on to the harness, which still writes its report and
# shuts its components down. tee ignores the signals so the console copy and the log stay complete.
run_harness() {
    local mode="$1" log
    log="$LOGDIR/harness-$mode.log"
    exec 3> >(trap '' INT TERM HUP; exec tee "$log")
    TEE_PID=$!
    harness_env
    (
        cd "$REPO_ROOT/harness" || exit 1
        exec env "${PROC_ENV[@]}" dotnet "$HARNESS_DLL" "$mode"
    ) >&3 2>&1 3>&- &
    HARNESS_PID=$!
    NAMES[$HARNESS_PID]=harness
    wait "$HARNESS_PID"
    code=$?
    HARNESS_PID=""
    exec 3>&-
    wait "$TEE_PID" 2>/dev/null
    TEE_PID=""
    if [ "$code" -ne 0 ]; then
        code=1
    fi
}

# run_facade_tests: the facade tests as facade/tests/run-tests.sh runs them, into a cleared
# artifacts/facade-tests/, so an old TRX is never read as this run's evidence. The status is recorded in
# exit-code and the run continues whatever it is; the harness reports the outcome.
run_facade_tests() {
    local results="$ART/facade-tests" log="$LOGDIR/facade-tests.log" status
    rm -rf -- "$results"
    mkdir -p "$results"
    echo "run-local.sh: running the facade tests (results $(rel "$results")/, log $(rel "$log"))"
    # No `--` before the test-application options: on SDK 10.0.403 `dotnet test` rejects
    # `-- ... --results-directory <dir>` when <dir> exists, and writes no TRX.
    (cd "$REPO_ROOT/facade" && dotnet test --project tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj \
        -c "$CONFIGURATION" --no-restore \
        --report-xunit-trx --report-xunit-trx-filename facade-tests.trx --results-directory "$results") 2>&1 |
        tee "$log"
    status=${PIPESTATUS[0]}
    printf '%s\n' "$status" >"$results/exit-code"
    echo "run-local.sh: facade tests exited $status"
    if [ ! -f "$results/facade-tests.trx" ]; then
        echo "run-local.sh: warning: $(rel "$results")/facade-tests.trx was not written" >&2
    fi
}

# The stand-in sets its own content root to its build output, so it needs no argument.
start_standin() {
    warn_busy standin "$STANDIN_REST_PORT" "$STANDIN_SOAP_PORT" "$STANDIN_ADMIN_PORT"
    standin_env
    start_bg standin standin "$STANDIN_DLL"
}

# The facade keeps the host's default content root, the working directory, so started from facade/ it
# would look for appsettings.json and its Routing section in facade/. --contentRoot names the build
# output, which holds them.
start_facade() {
    warn_busy facade "$FACADE_REST_PORT" "$FACADE_SOAP_PORT" "$FACADE_ADMIN_PORT"
    facade_env
    start_bg facade facade "$FACADE_DLL" \
        --contentRoot "$REPO_ROOT/facade/src/Erewhon.Facade/bin/$CONFIGURATION/$TFM"
}

# --- Subcommands ----------------------------------------------------------------------------------

# prepare: the checks and directories every subcommand needs, once its name is known to be valid.
prepare() {
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "run-local.sh: dotnet is not on PATH" >&2
        exit 1
    fi
    mkdir -p "$LOGDIR"
}

cmd_capture() {
    build standin src/Erewhon.StandIn/Erewhon.StandIn.csproj
    build harness src/Erewhon.Harness/Erewhon.Harness.csproj
    start_standin
    run_harness capture
}

cmd_export_openapi() {
    build facade src/Erewhon.Facade/Erewhon.Facade.csproj
    build harness src/Erewhon.Harness/Erewhon.Harness.csproj
    start_facade
    run_harness export-openapi
}

cmd_replay() {
    build standin src/Erewhon.StandIn/Erewhon.StandIn.csproj
    build facade src/Erewhon.Facade/Erewhon.Facade.csproj
    build harness src/Erewhon.Harness/Erewhon.Harness.csproj
    build facade tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj
    run_facade_tests
    # The stand-in is always started; BACKEND_* overrides change only where the facade forwards.
    start_standin
    start_facade
    run_harness replay
}

# --- Main -----------------------------------------------------------------------------------------

if [ "$#" -ne 1 ]; then
    usage
    exit 1
fi

code=1
case "$1" in
capture)
    prepare
    cmd_capture
    ;;
export-openapi)
    prepare
    cmd_export_openapi
    ;;
replay)
    prepare
    cmd_replay
    ;;
*)
    usage
    exit 1
    ;;
esac
cleanup
exit "$code"
