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
#   BACKEND_REST_URL, BACKEND_SOAP_URL  Where the facade forwards; default the local stand-in. In replay
#                         they are also the harness's backend URLs, as docker-compose.yml sets them, so
#                         the report names them and the backend WSDL is fetched from BACKEND_SOAP_URL.
#                         Capture ignores them and always targets the stand-in it starts.
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

# A CDPATH match would make `cd` print a path into the $(cd ... && pwd) resolutions below.
unset CDPATH

# The repository is located from the script's own real path: every symbolic link to the script is
# followed (plain readlink, so no GNU -f is needed) and `..` is resolved physically, so neither a link
# nor a linked directory moves the root. The root is confirmed before anything is written under it.
INVOKED_AS="${BASH_SOURCE[0]:-}"
no_repository() {
    echo "run-local.sh: cannot locate the repository: $1${INVOKED_AS:+ (invoked as $INVOKED_AS)}" >&2
    exit 1
}
[ -n "$INVOKED_AS" ] || no_repository "the script was not run from its own file"
SCRIPT_PATH="$INVOKED_AS"
LINK_HOPS=0
while [ -L "$SCRIPT_PATH" ]; do
    LINK_HOPS=$((LINK_HOPS + 1))
    [ "$LINK_HOPS" -le 40 ] || no_repository "more than 40 symbolic links lead to the script"
    LINK_TARGET="$(readlink -- "$SCRIPT_PATH")" ||
        no_repository "cannot read the symbolic link $SCRIPT_PATH"
    case "$LINK_TARGET" in
    /*) SCRIPT_PATH="$LINK_TARGET" ;;
    *)
        # A relative target is taken from the physical directory that holds the link.
        LINK_DIR="$(cd -P -- "$(dirname -- "$SCRIPT_PATH")" 2>/dev/null && pwd -P)" ||
            no_repository "cannot enter the directory of $SCRIPT_PATH"
        SCRIPT_PATH="$LINK_DIR/$LINK_TARGET"
        ;;
    esac
done
SCRIPT_DIR="$(cd -P -- "$(dirname -- "$SCRIPT_PATH")" 2>/dev/null && pwd -P)" ||
    no_repository "cannot enter the directory of $SCRIPT_PATH"
REPO_ROOT="$(cd -P -- "$SCRIPT_DIR/../.." 2>/dev/null && pwd -P)" ||
    no_repository "cannot enter $SCRIPT_DIR/../.."
if ! [ "$REPO_ROOT/harness/scripts/run-local.sh" -ef "$SCRIPT_PATH" ]; then
    no_repository "$REPO_ROOT does not hold this script as harness/scripts/run-local.sh"
fi
# facade/ is not required: capture runs before any facade/ path exists.
if ! [ -d "$REPO_ROOT/standin" ] || ! [ -d "$REPO_ROOT/harness" ]; then
    no_repository "$REPO_ROOT does not have both a standin/ and a harness/ directory"
fi
ART="$REPO_ROOT/artifacts"
LOGDIR="$ART/local"
TFM="${TFM:-net10.0}"
readonly REPO_ROOT ART LOGDIR TFM
readonly CONFIGURATION=Release

readonly LOOPBACK=127.0.0.1
readonly STANDIN_REST_PORT=15000 STANDIN_SOAP_PORT=15001 STANDIN_ADMIN_PORT=15090
readonly FACADE_REST_PORT=65000 FACADE_SOAP_PORT=21001 FACADE_ADMIN_PORT=9090

# The backend the facade forwards to: BACKEND_REST_URL and BACKEND_SOAP_URL, each falling back to the
# local stand-in when unset or empty. Set once here so the facade and the replay harness never disagree.
readonly FORWARD_REST_URL="${BACKEND_REST_URL:-http://$LOOPBACK:$STANDIN_REST_PORT}"
readonly FORWARD_SOAP_URL="${BACKEND_SOAP_URL:-http://$LOOPBACK:$STANDIN_SOAP_PORT}"

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
        "Routing__Targets__backend__RestBaseUrl=$FORWARD_REST_URL"
        "Routing__Targets__backend__SoapBaseUrl=$FORWARD_SOAP_URL"
        "Backend__TimeoutSeconds=${BACKEND_TIMEOUT_SECONDS:-30}"
    )
}

# harness_env <mode>: the harness's settings for capture, export-openapi or replay.
harness_env() {
    # In replay the backend REST and SOAP URLs are the ones the facade forwards to, as docker-compose.yml
    # sets them, so the run facts, the external-backend banner and the WSDL fetch name the real backend.
    # Reset reaches the stand-in through the admin URL alone. Capture always targets the stand-in this
    # script started, whatever BACKEND_REST_URL and BACKEND_SOAP_URL say, so it never records another
    # backend into baseline/**; export-openapi reads no backend URL. The admin URL uses `-`, not `:-`: an
    # explicitly empty BACKEND_ADMIN_URL stays empty and marks an external backend.
    local mode="$1"
    local backend_rest="http://$LOOPBACK:$STANDIN_REST_PORT"
    local backend_soap="http://$LOOPBACK:$STANDIN_SOAP_PORT"
    if [ "$mode" = replay ]; then
        backend_rest="$FORWARD_REST_URL"
        backend_soap="$FORWARD_SOAP_URL"
    fi
    PROC_ENV=(
        "Harness__RepoRoot=$REPO_ROOT"
        "Harness__ExecutionMode=local-processes"
        "Harness__ReportPath=${HARNESS_REPORT_PATH:-$REPO_ROOT/RESULTS.md}"
        "Harness__Target__RestBaseUrl=http://$LOOPBACK:$FACADE_REST_PORT"
        "Harness__Target__SoapBaseUrl=http://$LOOPBACK:$FACADE_SOAP_PORT"
        "Harness__Target__AdminUrl=http://$LOOPBACK:$FACADE_ADMIN_PORT"
        "Harness__Backend__RestBaseUrl=$backend_rest"
        "Harness__Backend__SoapBaseUrl=$backend_soap"
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

# port_busy <port>: probes 127.0.0.1:<port> within 2 s and sets PROBE_STATUS to the probe's own exit
# status. The /dev/tcp path is bash's own connect syntax; nothing under /dev is opened or touched.
# Returns 0 when the connect succeeds (something already listens on the port), 1 when it is refused
# (probe status 1: the port is free) and 2 when the probe itself failed, so the port's state is unknown:
# it timed out (124), timeout or bash could not be run (125, 126, 127), or it ended on a signal.
port_busy() {
    # shellcheck disable=SC2016 # $0 and $1 are expanded by the inner bash, from its arguments.
    timeout 2 bash -c 'exec 3<>"/dev/tcp/$0/$1"' "$LOOPBACK" "$1" 2>/dev/null
    PROBE_STATUS=$?
    case "$PROBE_STATUS" in
    0) return 0 ;;
    1) return 1 ;;
    *) return 2 ;;
    esac
}

# warn_busy <name> <port>...: warns about ports a leftover process already holds, and about ports whose
# probe failed, since those may be held as well; a free port gets no message. The run continues.
warn_busy() {
    local name="$1" port
    shift
    for port in "$@"; do
        port_busy "$port"
        case $? in
        0)
            echo "run-local.sh: warning: $LOOPBACK:$port is already in use; $name may fail to start" >&2
            ;;
        2)
            echo "run-local.sh: warning: could not probe $LOOPBACK:$port (probe status $PROBE_STATUS);" \
                "$name may fail to start if the port is in use" >&2
            ;;
        esac
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
    harness_env "$mode"
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

# prepare: the checks and directories every subcommand needs, once its name is known to be valid. Every
# external program the script runs is looked up on PATH first, so a missing one stops the run with its
# name before anything is built, started or written. Unchecked, a missing timeout or bash would make
# every port probe fail, and a missing tee would lose the logs and the harness's console copy.
prepare() {
    local tool missing=()
    for tool in dotnet timeout bash tee env sed tail sleep mkdir rm basename dirname; do
        if ! type -P "$tool" >/dev/null 2>&1; then
            missing+=("$tool")
        fi
    done
    if [ "${#missing[@]}" -eq 1 ]; then
        echo "run-local.sh: required tool is not on PATH: ${missing[0]}" >&2
        exit 1
    elif [ "${#missing[@]}" -gt 1 ]; then
        echo "run-local.sh: required tools are not on PATH: ${missing[*]}" >&2
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
    # The stand-in is always started, and reset reaches it through the admin URL. BACKEND_REST_URL and
    # BACKEND_SOAP_URL change where the facade forwards and, as under Compose, the backend URLs the
    # harness reports and fetches the backend WSDL from.
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
