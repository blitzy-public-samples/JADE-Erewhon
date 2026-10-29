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
# Port collisions. The ports are fixed, so every run in one network namespace competes for them. Before
# starting a component the script warns about each of its ports a listener already holds and names that
# listener's process (ss). After starting it, the script polls every 0.5 s, for up to 60 s, until the
# pid it started owns every listener on those ports. Another process on one of them (a competing run, a
# process left by a SIGKILLed run, any other server), or the started pid exiting, ends the run with exit
# 1 before the harness starts, naming the port and its owner, so the harness never tests, resets or shuts
# down a process this run did not start. A component still running but not yet bound at 60 s is left to
# the harness's readiness check (CHK-READY). Immediately before the harness starts, every component the
# run started is checked again: it must still be running, and every listener on its ports must still be
# its own. One that has exited since its own check (the stand-in while the facade starts, for example),
# or a port another process has taken since, ends the run the same way; a port with no listener while
# its pid runs is again left to CHK-READY. A second run in the same checkout stops at once, before any
# build, on the lock artifacts/local/run-local.lock.
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
#   HARNESS_REPORT_PATH   Where replay writes the report; default <repository>/RESULTS.md. It must lie
#                         inside the repository: replay refuses any other path with CHK-REPORT and exit 1.
#   TFM                   Target-framework folder of the build output; default net10.0.
#
# The script never reads .env: its values are Compose service hostnames (http://standin:65000), which
# local processes cannot resolve. It adds no credential, token or authentication header anywhere.
#
# Every dotnet command runs from inside standin/, facade/ or harness/, so that component's global.json
# (SDK pin, Microsoft.Testing.Platform runner) applies; none runs from the repository root or contracts/.
# Logs go to artifacts/local/.
#
# Exit status: the harness's own, 0 or 1, and only ever from a harness that ran against the processes
# this run started. Usage errors, a missing tool, a second run in the same checkout, build failures, a
# port collision, a component that exits before the harness starts and interrupts exit 1. So does a log or a
# facade-test result this run cannot write or clear, so the harness never reads an earlier run's
# artifacts/facade-tests/ as this run's. Each such failure before the harness starts ends with a line
# naming what the run did not regenerate: the report (RESULTS.md, or HARNESS_REPORT_PATH) for replay,
# baseline/** and contracts/wsdl/ErewhonWebService.wsdl for capture, the OpenAPI document for
# export-openapi. RESULTS.md is never deleted. Failed or timed-out facade tests are not such a failure:
# their status goes to artifacts/facade-tests/exit-code and the harness reports them. A log its logger
# (tee) could not write in full is reported as an error and never changes the status.
#
# Supervision. Every build and the facade-test run is a background job in its own process group and
# session (setsid) that the script waits on, so a signal to the script is handled at once and stops the
# whole group: SIGTERM, then SIGKILL after 10 s. The facade-test run is limited to 600 s; at the limit it
# is stopped the same way, exit-code records 124 and the run continues to the harness. The loggers (tee)
# of the facade tests and the harness ignore INT, TERM and HUP; one still running 10 s after the script
# closes its input is killed by its pid (SIGKILL) and reaped, and its log is reported as incomplete.
# Every process the script starts is stopped by its pid or its process group on every exit path, never
# by name.

set -uo pipefail
# No job control, even under `bash -m`: background jobs stay in the script's process group, so the
# setsid that starts each supervised job never has to fork and the job's pid names its process group.
set +m

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
# Once SIGTERM has cancelled it, the harness reaches its final line within 105 s whatever harness.json or
# Harness__* configure: a seed restoration in flight 15 s, two report writes 30 s each and two component
# shutdowns 15 s each. Its Main then waits at most 5 s for its signal callbacks, so 120 s leaves a margin
# before SIGKILL.
readonly STOP_SECONDS=10
readonly HARNESS_STOP_SECONDS=120
# Bound on how long a started stand-in or facade may take to bind its ports (await_listeners).
readonly LISTEN_SECONDS=60
# Bound on the whole facade-test run. At the limit the tests are stopped like an interrupted job, exit-code
# records 124 and the run continues: the harness reports the missing or partial results.
readonly FACADE_TESTS_SECONDS=600

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# No persistent MSBuild node, MSBuild server or compiler server outlives a build, so the script leaves
# no process running that it did not stop. A value the caller set is kept.
export MSBUILDDISABLENODEREUSE="${MSBUILDDISABLENODEREUSE:-1}"
export DOTNET_CLI_USE_MSBUILD_SERVER="${DOTNET_CLI_USE_MSBUILD_SERVER:-0}"
export UseSharedCompilation="${UseSharedCompilation:-false}"

PIDS=()
declare -A NAMES=()
# The fixed ports of each started stand-in or facade, by pid, space-separated, which recheck_started
# checks again before the harness starts.
declare -A PORTS=()
PROC_ENV=()
# The pid start_bg started last, which await_listeners checks.
STARTED_PID=""
HARNESS_PID=""
TEE_PID=""
# The supervised build or facade-test job in flight: its pid, which is also its process group, and the
# name messages give it.
CHILD_PID=""
CHILD_NAME=""
# The logger (tee) of the facade-test run while it is open; TEE_PID is the harness's, writing HARNESS_LOG.
TEST_TEE_PID=""
HARNESS_LOG=""
# What the subcommand's harness run writes, as on_exit names it, and whether the harness has started.
OUTPUTS=""
HARNESS_STARTED=0
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

# open_log <log>: creates or empties <log> before the step that writes it starts, and ends the run with 1
# when that fails, so no step runs without its log.
open_log() {
    if ! : >"$1"; then
        echo "run-local.sh: cannot write the log $(rel "$1"); the step that writes it is not started" >&2
        exit 1
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
    open_log "$log"
    echo "run-local.sh: building $component/$project ($CONFIGURATION, log $(rel "$log"))"
    (cd "$REPO_ROOT/$component" && exec setsid -w dotnet build "$project" -c "$CONFIGURATION" "$@") \
        >"$log" 2>&1 &
    supervise "$!" "the $component build of $project"
    status=$?
    if [ "$status" -ne 0 ]; then
        echo "run-local.sh: build failed: $component $project" >&2
        quote_tail 40 "$log"
        exit 1
    fi
}

# listeners <port>: prints one ss line per TCP listener on <port> in this network namespace, each
# naming its process as users:(("<name>",pid=<pid>,fd=<fd>)); prints nothing when the port is free.
# Fails, with ss's own message on stderr, only when ss cannot list the sockets.
listeners() {
    ss -ltnpH "sport = :$1"
}

# owners <ss lines>: names every process the lines show, as "<name> pid <pid>" joined by ", ", or
# "a process ss cannot name" when the lines show none.
owners() {
    local rest="$1" owner text="" re='\("([^"]*)",pid=([0-9]+),'
    while [[ $rest =~ $re ]]; do
        owner="${BASH_REMATCH[1]} pid ${BASH_REMATCH[2]}"
        case ", $text, " in
        *", $owner, "*) ;;
        *) text="${text:+$text, }$owner" ;;
        esac
        rest="${rest#*"${BASH_REMATCH[0]}"}"
    done
    printf '%s' "${text:-a process ss cannot name}"
}

# warn_busy <name> <port>...: warns about each port a listener already holds, naming its process, and
# about each port whose listeners ss cannot list, since that port may be held as well; a free port gets
# no message. The run continues, and await_listeners then ends it if another process holds the port.
warn_busy() {
    local name="$1" port lines
    shift
    for port in "$@"; do
        if ! lines="$(listeners "$port")"; then
            echo "run-local.sh: warning: could not list the listeners on $LOOPBACK:$port; $name may fail to start if the port is in use" >&2
        elif [ -n "$lines" ]; then
            echo "run-local.sh: warning: $LOOPBACK:$port is already in use by $(owners "$lines"); $name may fail to start" >&2
        fi
    done
}

# start_bg <name> <component> <dll path relative to the component> [application arguments]
# Starts the process with the settings the caller just put in PROC_ENV and sets STARTED_PID to its pid.
# exec (and env's own exec) make the background subshell's pid the dotnet process itself, so $! is the
# pid to stop and the pid ss shows on the process's listeners; the caller then runs await_listeners.
# The process gets no copy of the checkout lock (fd 9), so one that outlives a SIGKILLed run never
# blocks the next run, whose await_listeners names it instead. Readiness is the harness's: it polls
# each /health within its readiness limit.
start_bg() {
    local name="$1" component="$2" dll="$3" log pid
    shift 3
    log="$LOGDIR/$name.log"
    (
        cd "$REPO_ROOT/$component" || exit 1
        exec env "${PROC_ENV[@]}" dotnet "$dll" "$@"
    ) >"$log" 2>&1 9>&- &
    pid=$!
    PIDS+=("$pid")
    NAMES[$pid]="$name"
    STARTED_PID="$pid"
    echo "started $name pid $pid (log $(rel "$log"))"
}

# await_listeners <name> <pid> <port>...: polls every 0.5 s, for up to LISTEN_SECONDS, until <pid> owns
# every listener on each <port>. Another process on one of the ports (a competing run, a process left
# by a SIGKILLed run, any other server), or <pid> exiting, ends the run with exit 1 before the harness
# starts, so the harness never tests, resets or shuts down a process this run did not start; the EXIT
# trap then stops what this run started. A pid still running at the limit, with no other process on
# its ports, is left to the harness's readiness check, which records CHK-READY.
await_listeners() {
    local name="$1" pid="$2" log port lines foreign held pending list ticks=0
    shift 2
    log="$LOGDIR/$name.log"
    printf -v list '%s, ' "$@"
    list="${list%, }"
    while :; do
        held=""
        pending=""
        for port in "$@"; do
            if ! lines="$(listeners "$port")"; then
                echo "run-local.sh: ss cannot list the listeners on $LOOPBACK:$port, so $name pid $pid cannot be shown to own them; the harness is not run" >&2
                exit 1
            fi
            if [ -z "$lines" ]; then
                pending="${pending:+$pending, }$port"
                continue
            fi
            foreign="$(printf '%s\n' "$lines" | grep -vF "pid=$pid,")"
            if [ -n "$foreign" ]; then
                held="${held:+$held; }$LOOPBACK:$port is held by $(owners "$foreign")"
            fi
        done
        if [ -n "$held" ]; then
            echo "run-local.sh: port collision for $name pid $pid: $held; the harness is not run, so it touches no process this run did not start; last lines of $(rel "$log"):" >&2
            quote_tail 20 "$log"
            exit 1
        fi
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "run-local.sh: $name pid $pid exited during start-up; the harness is not run; last lines of $(rel "$log"):" >&2
            quote_tail 20 "$log"
            exit 1
        fi
        if [ -z "$pending" ]; then
            echo "run-local.sh: $name pid $pid owns $LOOPBACK ports $list"
            return 0
        fi
        if [ "$ticks" -ge $((LISTEN_SECONDS * 2)) ]; then
            echo "run-local.sh: warning: $name pid $pid is running but has not bound $LOOPBACK ports $pending within $LISTEN_SECONDS s, and no other process holds them; the harness's readiness check decides" >&2
            return 0
        fi
        sleep 0.5
        ticks=$((ticks + 1))
    done
}

# recheck_started: the hand-off check, run immediately before run_harness. await_listeners checks each
# component once, at its own start-up, but one can exit afterwards (while a later component starts, for
# example) and another process can then take its ports. So every component this run started must still
# be running, and every listener on each of its ports must still be its own. A component that has
# exited, a port another process holds, or ss failing ends the run with exit 1 before the harness
# starts, so the harness never tests, resets or shuts down a process this run did not start; the EXIT
# trap then stops what this run started. A port with no listener while its pid runs is allowed, as in
# await_listeners, and left to the harness's readiness check, which records CHK-READY. Every component
# is checked before the run ends, so the messages name each one that failed.
recheck_started() {
    local pid name log port lines foreign held failed=0
    local -a ports
    for pid in "${PIDS[@]}"; do
        name="${NAMES[$pid]}"
        log="$LOGDIR/$name.log"
        read -r -a ports <<<"${PORTS[$pid]-}"
        held=""
        for port in "${ports[@]}"; do
            if ! lines="$(listeners "$port")"; then
                echo "run-local.sh: ss cannot list the listeners on $LOOPBACK:$port, so $name pid $pid cannot be shown to own them; the harness is not run" >&2
                exit 1
            fi
            if [ -z "$lines" ]; then
                continue
            fi
            foreign="$(printf '%s\n' "$lines" | grep -vF "pid=$pid,")"
            if [ -n "$foreign" ]; then
                held="${held:+$held; }$LOOPBACK:$port is held by $(owners "$foreign")"
            fi
        done
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "run-local.sh: $name pid $pid exited after its start-up check${held:+, and $held}; the harness is not run, so it touches no process this run did not start; last lines of $(rel "$log"):" >&2
        elif [ -n "$held" ]; then
            echo "run-local.sh: port collision for $name pid $pid: $held; the harness is not run, so it touches no process this run did not start; last lines of $(rel "$log"):" >&2
        else
            continue
        fi
        quote_tail 20 "$log"
        failed=1
    done
    if [ "$failed" -ne 0 ]; then
        exit 1
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

# --- Supervised jobs ------------------------------------------------------------------------------
# Every build and the facade-test run is a supervised job: a background subshell that enters its
# component directory and execs `setsid -w dotnet ...`. Background jobs share the script's process
# group, so setsid does not fork: the job's pid also names a new process group and session holding the
# dotnet process and everything it starts (restore, MSBuild nodes, the test host) and nothing else.
# CHILD_PID records the job in flight, so cleanup stops that whole group first. The script waits on a
# job with the wait builtin or a 0.5 s poll, so a signal to the script is handled at once rather than
# after dotnet ends. Signals go to the group only, never to a bare pid, and never by process name.

# group_alive <pgid>: true while any process of the group exists.
group_alive() {
    kill -0 -- "-$1" 2>/dev/null
}

# stop_group <pgid> <name>: SIGTERM to the whole group, with SIGCONT so a stopped member can act on it,
# then SIGKILL after STOP_SECONDS, then reaps the job. A job interrupted in the instant before its setsid
# runs gets up to 2 s to create its group first. Returns 1 when a member is still running.
stop_group() {
    local pgid="$1" name="$2" ticks=0
    while ! group_alive "$pgid" && kill -0 "$pgid" 2>/dev/null && [ "$ticks" -lt 40 ]; do
        sleep 0.05
        ticks=$((ticks + 1))
    done
    if group_alive "$pgid"; then
        kill -TERM -- "-$pgid" 2>/dev/null
        kill -CONT -- "-$pgid" 2>/dev/null
        ticks=0
        while group_alive "$pgid" && [ "$ticks" -lt $((STOP_SECONDS * 2)) ]; do
            sleep 0.5
            ticks=$((ticks + 1))
        done
        if group_alive "$pgid"; then
            kill -KILL -- "-$pgid" 2>/dev/null
            ticks=0
            while group_alive "$pgid" && [ "$ticks" -lt 10 ]; do
                sleep 0.5
                ticks=$((ticks + 1))
            done
        fi
    fi
    if group_alive "$pgid"; then
        echo "run-local.sh: $name (process group $pgid) still running" >&2
        return 1
    fi
    wait "$pgid" 2>/dev/null
    return 0
}

# supervise <pid> <name> [deadline seconds]: waits for the supervised job <pid> and returns its status.
# A job still running at the deadline is stopped and its status is 124. Processes left in the job's
# group after the job itself ends are stopped as well.
supervise() {
    local pid="$1" name="$2" deadline="${3:-0}" status start
    CHILD_PID="$pid"
    CHILD_NAME="$name"
    if [ "$deadline" -gt 0 ]; then
        start=$SECONDS
        while kill -0 "$pid" 2>/dev/null && [ $((SECONDS - start)) -lt "$deadline" ]; do
            sleep 0.5
        done
    fi
    if [ "$deadline" -gt 0 ] && kill -0 "$pid" 2>/dev/null; then
        echo "run-local.sh: $name did not finish within $deadline s; stopping process group $pid" >&2
        stop_group "$pid" "$name"
        status=124
    else
        wait "$pid"
        status=$?
        if group_alive "$pid"; then
            echo "run-local.sh: $name left processes in process group $pid; stopping them" >&2
            stop_group "$pid" "$name"
        fi
    fi
    CHILD_PID=""
    CHILD_NAME=""
    return "$status"
}

# close_tee <fd> <pid> <log> <whose>: closes the script's end of a logger's pipe, so the logger (tee)
# reaches end of input once the step writing to it has ended, and reaps it. A logger still running
# STOP_SECONDS later (stopped, or blocked writing) is killed by its pid with SIGKILL, because it ignores
# INT, TERM and HUP, and is reaped within 5 s more. tee keeps copying to the console whatever happens to
# the file, so a logger that failed leaves <log> incomplete while the console holds <whose> full output; a
# killed logger leaves both copies without what it had not copied yet. Either is reported as an error, and
# the step's own status is never changed. Returns 0 when the log is complete, 1 when it is not and the
# logger is gone, and 2 when the logger is still running after SIGKILL: the caller then keeps its pid, so
# that cleanup tries again.
close_tee() {
    local fd="$1" pid="$2" log="$3" whose="$4" start=$SECONDS status ticks=0
    exec {fd}>&-
    while kill -0 "$pid" 2>/dev/null && [ $((SECONDS - start)) -lt "$STOP_SECONDS" ]; do
        sleep 0.1
    done
    if kill -0 "$pid" 2>/dev/null; then
        kill -KILL "$pid" 2>/dev/null
        while kill -0 "$pid" 2>/dev/null && [ "$ticks" -lt 50 ]; do
            sleep 0.1
            ticks=$((ticks + 1))
        done
        if kill -0 "$pid" 2>/dev/null; then
            echo "run-local.sh: error: the logger of $(rel "$log") (pid $pid) is still running after SIGKILL, so" \
                "the log may be incomplete; the console holds $whose output as far as the logger copied it" >&2
            return 2
        fi
        wait "$pid" 2>/dev/null
        echo "run-local.sh: error: the logger of $(rel "$log") (pid $pid) did not end within $STOP_SECONDS s and" \
            "was killed, so the log may be incomplete; the console holds $whose output as far as the logger" \
            "copied it" >&2
        return 1
    fi
    wait "$pid"
    status=$?
    if [ "$status" -ne 0 ]; then
        echo "run-local.sh: error: the log $(rel "$log") is incomplete (its logger exited $status);" \
            "the console holds $whose full output" >&2
        return 1
    fi
    return 0
}

# cleanup: stops, once, the supervised build or facade-test job in flight and reaps its logger, then the
# harness if it is still running and its logger, then every started process in reverse start order (the
# facade before the stand-in). A logger pid is dropped only once close_tee has seen that logger gone.
cleanup() {
    local i
    if [ "$CLEANED" -eq 1 ]; then
        return 0
    fi
    CLEANED=1
    if [ -n "$CHILD_PID" ]; then
        echo "run-local.sh: stopping $CHILD_NAME (process group $CHILD_PID)" >&2
        stop_group "$CHILD_PID" "$CHILD_NAME"
        CHILD_PID=""
    fi
    if [ -n "$TEST_TEE_PID" ]; then
        close_tee 4 "$TEST_TEE_PID" "$LOGDIR/facade-tests.log" "the facade tests'"
        [ "$?" -eq 2 ] || TEST_TEE_PID=""
    fi
    if [ -n "$HARNESS_PID" ]; then
        stop_pid "$HARNESS_PID" "$HARNESS_STOP_SECONDS"
        HARNESS_PID=""
    fi
    if [ -n "$TEE_PID" ]; then
        close_tee 3 "$TEE_PID" "$HARNESS_LOG" "the harness's"
        [ "$?" -eq 2 ] || TEE_PID=""
    fi
    for ((i = ${#PIDS[@]} - 1; i >= 0; i--)); do
        stop_pid "${PIDS[i]}"
    done
}

# shellcheck disable=SC2317 # Invoked only through the INT, TERM and HUP trap below.
on_signal() {
    trap '' INT TERM HUP
    # A signal during a cleanup already under way (the EXIT path, or the last step of a finished run)
    # returns to it, so that cleanup still stops everything; exiting here would orphan what it had not
    # reached yet.
    if [ "$CLEANED" -eq 1 ]; then
        echo "run-local.sh: interrupted; still stopping every process this run started" >&2
        return 0
    fi
    echo "run-local.sh: interrupted; stopping every process this run started" >&2
    cleanup
    exit 1
}

# on_exit: the EXIT trap. Takes the script's status first, then stops what is left through cleanup.
# When the run ends nonzero before the harness has started (a build, setup, log, start-up or tool
# failure, or an interrupt), it names what this subcommand's harness run would have written, so an
# earlier run's report is never taken for this one's. The status is kept.
# shellcheck disable=SC2317 # Invoked only through the EXIT trap below.
on_exit() {
    local status=$?
    cleanup
    if [ "$status" -ne 0 ] && [ "$HARNESS_STARTED" -eq 0 ] && [ -n "$OUTPUTS" ]; then
        echo "run-local.sh: $OUTPUTS not regenerated by this run" >&2
    fi
    exit "$status"
}

trap on_exit EXIT
trap on_signal INT TERM HUP

# run_harness <mode>: runs the harness and sets `code` to its status, 0 or 1. Its output stays on the
# console, which report recovery depends on, and is also kept in artifacts/local/harness-<mode>.log.
# The harness runs as a background job that the script waits on, so a signal to the script interrupts
# the wait at once and cleanup passes SIGTERM on to the harness, which still writes its report and
# shuts its components down. tee ignores the signals so the console copy and the log stay complete.
run_harness() {
    local mode="$1" log
    log="$LOGDIR/harness-$mode.log"
    open_log "$log"
    HARNESS_LOG="$log"
    exec 3> >(trap '' INT TERM HUP; exec tee "$log")
    TEE_PID=$!
    harness_env "$mode"
    # From here the harness owns the report and on_exit no longer names it as not regenerated.
    HARNESS_STARTED=1
    (
        cd "$REPO_ROOT/harness" || exit 1
        exec env "${PROC_ENV[@]}" dotnet "$HARNESS_DLL" "$mode"
    ) >&3 2>&1 3>&- &
    HARNESS_PID=$!
    NAMES[$HARNESS_PID]=harness
    wait "$HARNESS_PID"
    code=$?
    HARNESS_PID=""
    # A log the logger could not write is reported, and the harness's code stays the script's status. A
    # logger still running after SIGKILL keeps its pid in TEE_PID, so cleanup tries again.
    close_tee 3 "$TEE_PID" "$log" "the harness's"
    [ "$?" -eq 2 ] || TEE_PID=""
    if [ "$code" -ne 0 ]; then
        code=1
    fi
}

# refuse_evidence <reason>: ends the run with 1, before the harness starts, when this run cannot establish
# its own facade-test results, so the harness never reads an earlier run's TRX or exit-code, or none.
refuse_evidence() {
    echo "run-local.sh: $1; stale facade-test evidence refused, the harness is not run" >&2
    exit 1
}

# clear_facade_results: removes artifacts/facade-tests/ and checks that nothing of it is left.
clear_facade_results() {
    local results="$ART/facade-tests"
    if ! rm -rf -- "$results" || [ -e "$results" ] || [ -L "$results" ]; then
        refuse_evidence "cannot remove $(rel "$results")/"
    fi
}

# run_facade_tests: the facade tests as facade/tests/run-tests.sh runs them, into a cleared
# artifacts/facade-tests/, so an old TRX is never read as this run's evidence. The run is a supervised
# job bounded by FACADE_TESTS_SECONDS; at the limit it is stopped and its status is 124. The status is
# recorded in exit-code and the run continues whatever it is; the harness reports the outcome.
run_facade_tests() {
    local results="$ART/facade-tests" log="$LOGDIR/facade-tests.log" status
    clear_facade_results
    if ! mkdir -p -- "$results"; then
        refuse_evidence "cannot create $(rel "$results")/"
    fi
    open_log "$log"
    echo "run-local.sh: running the facade tests (results $(rel "$results")/, log $(rel "$log")," \
        "limit $FACADE_TESTS_SECONDS s)"
    # The console copy goes through a logger the script tracks, as for the harness; tee ignores the
    # signals so both copies stay complete while cleanup stops the tests.
    exec 4> >(trap '' INT TERM HUP; exec tee "$log")
    TEST_TEE_PID=$!
    # No `--` before the test-application options: on SDK 10.0.403 `dotnet test` rejects
    # `-- ... --results-directory <dir>` when <dir> exists, and writes no TRX.
    (cd "$REPO_ROOT/facade" && exec setsid -w dotnet test \
        --project tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj -c "$CONFIGURATION" --no-restore \
        --report-xunit-trx --report-xunit-trx-filename facade-tests.trx \
        --report-xunit-xml --report-xunit-xml-filename facade-tests.xunit.xml --results-directory "$results") \
        >&4 2>&1 4>&- &
    supervise "$!" "the facade tests" "$FACADE_TESTS_SECONDS"
    status=$?
    # A logger still running after SIGKILL keeps its pid in TEST_TEE_PID, so cleanup tries again.
    close_tee 4 "$TEST_TEE_PID" "$log" "the facade tests'"
    [ "$?" -eq 2 ] || TEST_TEE_PID=""
    if ! printf '%s\n' "$status" >"$results/exit-code"; then
        refuse_evidence "cannot write $(rel "$results")/exit-code (the facade tests exited $status)"
    fi
    echo "run-local.sh: facade tests exited $status"
    if [ ! -f "$results/facade-tests.trx" ]; then
        echo "run-local.sh: warning: $(rel "$results")/facade-tests.trx was not written" >&2
    fi
    if [ ! -f "$results/facade-tests.xunit.xml" ]; then
        echo "run-local.sh: warning: $(rel "$results")/facade-tests.xunit.xml was not written" >&2
    fi
}

# The stand-in sets its own content root to its build output, so it needs no argument.
start_standin() {
    warn_busy standin "$STANDIN_REST_PORT" "$STANDIN_SOAP_PORT" "$STANDIN_ADMIN_PORT"
    standin_env
    start_bg standin standin "$STANDIN_DLL"
    PORTS[$STARTED_PID]="$STANDIN_REST_PORT $STANDIN_SOAP_PORT $STANDIN_ADMIN_PORT"
    await_listeners standin "$STARTED_PID" "$STANDIN_REST_PORT" "$STANDIN_SOAP_PORT" "$STANDIN_ADMIN_PORT"
}

# The facade keeps the host's default content root, the working directory, so started from facade/ it
# would look for appsettings.json and its Routing section in facade/. --contentRoot names the build
# output, which holds them.
start_facade() {
    warn_busy facade "$FACADE_REST_PORT" "$FACADE_SOAP_PORT" "$FACADE_ADMIN_PORT"
    facade_env
    start_bg facade facade "$FACADE_DLL" \
        --contentRoot "$REPO_ROOT/facade/src/Erewhon.Facade/bin/$CONFIGURATION/$TFM"
    PORTS[$STARTED_PID]="$FACADE_REST_PORT $FACADE_SOAP_PORT $FACADE_ADMIN_PORT"
    await_listeners facade "$STARTED_PID" "$FACADE_REST_PORT" "$FACADE_SOAP_PORT" "$FACADE_ADMIN_PORT"
}

# --- Subcommands ----------------------------------------------------------------------------------

# prepare: the checks and directories every subcommand needs, once its name is known to be valid. Every
# external program the script runs is looked up on PATH first, so a missing one stops the run with its
# name before anything is built, started or written. Unchecked, a missing setsid would leave builds and
# tests unsupervised, a missing ss or grep would make every port-ownership check fail, a missing flock
# would let a second run into this checkout, and a missing tee would lose the logs and the harness's
# console copy.
prepare() {
    local tool missing=()
    for tool in dotnet setsid ss flock grep tee env sed tail sleep mkdir rm basename dirname; do
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
    # One run per checkout: fd 9 holds an exclusive lock on artifacts/local/run-local.lock until the
    # script exits, so a second run here stops before it builds or writes anything. The lock is not
    # host-wide; await_listeners tells apart runs of other checkouts. The stand-in, the facade and the
    # harness are started without fd 9, so one left running by a SIGKILLed run never holds the lock and
    # the next run's await_listeners names it instead.
    if ! exec 9>>"$LOGDIR/run-local.lock"; then
        echo "run-local.sh: cannot open $(rel "$LOGDIR/run-local.lock")" >&2
        exit 1
    fi
    if ! flock -n 9; then
        echo "run-local.sh: another run-local.sh run in this checkout holds $(rel "$LOGDIR/run-local.lock"); this run stops before building or writing anything" >&2
        exit 1
    fi
}

cmd_capture() {
    build standin src/Erewhon.StandIn/Erewhon.StandIn.csproj
    build harness src/Erewhon.Harness/Erewhon.Harness.csproj
    start_standin
    recheck_started
    # 9>&-: the harness and its logger get no copy of the checkout lock. bash keeps the lock held for
    # the call through a close-on-exec copy of fd 9 that no started program inherits.
    run_harness capture 9>&-
}

cmd_export_openapi() {
    build facade src/Erewhon.Facade/Erewhon.Facade.csproj
    build harness src/Erewhon.Harness/Erewhon.Harness.csproj
    start_facade
    recheck_started
    run_harness export-openapi 9>&-
}

cmd_replay() {
    # Cleared before any build, so a run that fails before its own facade tests leaves no earlier
    # run's TRX or exit-code behind.
    clear_facade_results
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
    # The stand-in was checked before the facade started; both are checked again at the hand-off.
    recheck_started
    run_harness replay 9>&-
}

# --- Main -----------------------------------------------------------------------------------------

if [ "$#" -ne 1 ]; then
    usage
    exit 1
fi

code=1
case "$1" in
capture)
    OUTPUTS="baseline/** and contracts/wsdl/ErewhonWebService.wsdl were"
    prepare
    cmd_capture
    ;;
export-openapi)
    OUTPUTS="contracts/openapi/erewhon-rest.openapi.json was"
    prepare
    cmd_export_openapi
    ;;
replay)
    OUTPUTS="$(rel "${HARNESS_REPORT_PATH:-$REPO_ROOT/RESULTS.md}") was"
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
