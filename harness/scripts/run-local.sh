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
# A started component has up to 60 s to bind its ports; one not bound by then is left to CHK-READY.
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
# facade-test status this run cannot write, and an artifacts/facade-tests/ or a private staging directory
# for the facade tests it cannot clear or create, so the harness never reads an earlier run's
# artifacts/facade-tests/ as this run's. So does artifacts/ or artifacts/local/ when it is a symbolic
# link or not a directory, and artifacts/local/run-local.lock when it is a symbolic link or not a
# regular file; nothing is written through any of them. Each such failure before the harness starts
# ends with a line naming what the run did not regenerate: the report (RESULTS.md, or
# HARNESS_REPORT_PATH) for replay, baseline/** and contracts/wsdl/ErewhonWebService.wsdl for capture,
# the OpenAPI document for export-openapi. RESULTS.md is never deleted. Failed facade tests, and tests
# stopped at their 600 s limit (status 124), are not such a failure: their status goes to
# artifacts/facade-tests/exit-code and the harness reports them. Nor is a TRX or xunit XML the tests did
# not write as a regular file, which is not published: the harness reports a missing TRX as
# CHK-FACADE-TRX. A log its logger (tee) could not write in full is reported as an error and never
# changes the status.
#
# Every started process is stopped by its pid or process group on every exit path, never by name.

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
# By default MSBuild node reuse, the MSBuild server and the compiler server are off, so none outlives
# the run or is shared with other host builds; non-empty caller values are kept and can re-enable them.
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
# Descriptors of the directories every output is written through (hold_dir): the repository root
# while prepare opens artifacts/, then artifacts/ and artifacts/local/ for the rest of the run, and
# artifacts/facade-tests/ and the facade tests' staging directory while the facade tests run and their
# results and status are published. STAGE_NAME is the staging directory's name in artifacts/, and
# STAGE_PRIVATE is 1 once it has passed its check (make_staging).
ROOT_FD=""
ART_FD=""
LOG_FD=""
RESULTS_FD=""
STAGE_FD=""
STAGE_NAME=""
STAGE_PRIVATE=0
# What hold_dir, create_temp and open_log hand their caller.
HELD_FD=""
TEMP_NAME=""
TEMP_FD=""
LOG_OUT=""

usage() {
    echo "usage: harness/scripts/run-local.sh capture|export-openapi|replay" >&2
}

# Prints a repository path relative to the repository root.
rel() {
    printf '%s' "${1#"$REPO_ROOT"/}"
}

# --- Output confinement ---------------------------------------------------------------------------
# Everything the script itself writes goes under artifacts/, which another process may have replaced,
# in whole or in part, by symbolic links. So nothing there is reached by its path again: artifacts/,
# artifacts/local/ and artifacts/facade-tests/ are held open (hold_dir), and each name is resolved inside
# a held directory through /proc/$$/fd/<n>/<name>, so no path above the held directory is resolved
# again. $$ is the main shell even in a subshell, and it holds those descriptors, so the same path also
# serves a command the script runs. No existing name is opened for writing: each file is created
# exclusively under a fresh name and renamed into place (create_temp, publish_temp), the lock is opened
# read-only (open_lock), and a directory is cleared by its name alone. The facade tests create their TRX
# and xunit XML by name, so they are given only a private directory of their own, made under a fresh
# name with mode 700 (make_staging), and the two files are renamed from it into artifacts/facade-tests/
# (publish_result).

# refuse_output <path> <what it is>: ends the run with 1 when an output path is not what this run writes
# through, a symbolic link or not a directory (not a regular file, for the lock). Nothing was written
# through it, and on_exit then names what the run did not regenerate.
refuse_output() {
    echo "run-local.sh: $(rel "$1") is $2; this run writes nothing through it" >&2
    exit 1
}

# hold_dir <parent fd> <path>: opens the directory <path>, whose parent is held open as <parent fd>,
# creating it when absent, and sets HELD_FD to its descriptor; <path> is the physical path it must
# have. A symbolic link or any other entry that is not a directory is refused (refuse_output). An
# absent one is created with mkdir, which never follows a symbolic link and fails on any entry already
# there, so one that a concurrent creation put there is checked below like any other. It is opened as
# `<name>/.`, which fails on anything but a directory rather than opening it (or blocking on a FIFO),
# and kept only when /proc shows a directory at <path>: one reached through a link put there since the
# checks has another physical path and is refused.
hold_dir() {
    local parent="$1" path="$2" entry fd
    entry="/proc/$$/fd/$parent/${path##*/}"
    if [ -L "$entry" ]; then
        refuse_output "$path" "a symbolic link"
    elif [ -e "$entry" ] && [ ! -d "$entry" ]; then
        refuse_output "$path" "not a directory"
    elif [ ! -e "$entry" ]; then
        mkdir -- "$entry" 2>/dev/null
    fi
    if ! { exec {fd}<"$entry/."; } 2>/dev/null; then
        echo "run-local.sh: cannot create or open $(rel "$path")/; this run writes nothing through it" >&2
        exit 1
    fi
    if [ ! -d "/proc/$$/fd/$fd" ] || [ "$(readlink -- "/proc/$$/fd/$fd")" != "$path" ]; then
        exec {fd}<&-
        refuse_output "$path" "no longer the directory it was checked to be"
    fi
    HELD_FD="$fd"
}

# has_srandom: true when bash provides SRANDOM (getrandom(2)), which bash 5.1 added.
has_srandom() {
    ((BASH_VERSINFO[0] > 5 || (BASH_VERSINFO[0] == 5 && BASH_VERSINFO[1] >= 1)))
}

# random_name_part: prints 16 hex digits (64 bits) from the kernel's random number generator, through
# SRANDOM or else /dev/urandom (od, tr). False when neither yields them.
random_name_part() {
    local part=""
    if has_srandom; then
        printf -v part '%08x%08x' "$SRANDOM" "$SRANDOM"
    else
        part="$(od -An -N8 -tx1 /dev/urandom 2>/dev/null | tr -d ' \n')"
    fi
    [[ "$part" =~ ^[0-9a-f]{16}$ ]] || return 1
    printf '%s\n' "$part"
}

# create_temp <dir fd> <name>: creates a new, empty regular file in the held directory <dir fd> under the
# fresh name .<name>.<random_name_part>, and sets TEMP_NAME to that name and TEMP_FD to the write
# descriptor of its creating open. The name is refused when any entry, a dangling symbolic link
# included, is already there. It is then created under noclobber: for a name with no entry, bash's
# noclobber open is O_CREAT|O_EXCL, which never follows a symbolic link and fails on any entry that
# appears in between. The random part keeps anyone from placing in advance the one kind of entry that
# noclobber opens without O_EXCL, a non-regular one. noclobber is local to the call. False when nothing
# was created.
create_temp() {
    local -
    local entry fd part
    part="$(random_name_part)" || return 1
    TEMP_NAME=".$2.$part"
    entry="/proc/$$/fd/$1/$TEMP_NAME"
    if [ -e "$entry" ] || [ -L "$entry" ]; then
        return 1
    fi
    set -C
    { exec {fd}>"$entry"; } 2>/dev/null || return 1
    TEMP_FD="$fd"
}

# publish_temp <dir fd> <name>: renames TEMP_NAME over <name> in the held directory <dir fd>. mv -fT
# renames with rename(2), which replaces a file or symbolic link at <name> without following it and fails
# on a directory, so <name> names the old entry until it names the new file, whole as it was when
# renamed. On failure TEMP_NAME is unlinked; the caller still closes TEMP_FD. False when <name> was not
# published.
publish_temp() {
    local dir="/proc/$$/fd/$1"
    if ! mv -fT -- "$dir/$TEMP_NAME" "$dir/$2" 2>/dev/null; then
        rm -f -- "$dir/$TEMP_NAME"
        return 1
    fi
}

# quote_tail <count> <log name>: prints the last <count> lines of artifacts/local/<log name> to stderr,
# each quoted with "> ". The log is reached through the held artifacts/local/ and read only while it is
# a regular file, so a symbolic link put in its place is never followed.
quote_tail() {
    local count="$1" file="/proc/$$/fd/$LOG_FD/$2"
    if [ -f "$file" ] && [ ! -L "$file" ]; then
        tail -n "$count" "$file" | sed 's/^/> /' >&2
    fi
}

# open_log <log name>: creates artifacts/local/<log name> afresh, a new empty regular file, before the
# step that writes it starts, and sets LOG_OUT to the descriptor that created it: the step writes the
# log through that descriptor alone, never through its path. An earlier log, or a symbolic link put in
# its place, is replaced and never opened or truncated (create_temp, publish_temp). Ends the run with 1
# when that fails, so no step runs without its log.
open_log() {
    if create_temp "$LOG_FD" "$1"; then
        if publish_temp "$LOG_FD" "$1"; then
            LOG_OUT="$TEMP_FD"
            return 0
        fi
        exec {TEMP_FD}>&-
    fi
    echo "run-local.sh: cannot write the log $(rel "$LOGDIR/$1"); the step that writes it is not started" >&2
    exit 1
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

# build <component> <project path relative to the component> [extra dotnet build arguments]
# Restore is implicit and deliberately not locked: the first local build writes the committed
# packages.lock.json files, and the Dockerfiles enforce locked mode.
build() {
    local component="$1" project="$2" log logfd pid status
    shift 2
    log="build-$component-$(basename "$project" .csproj).log"
    open_log "$log"
    logfd="$LOG_OUT"
    echo "run-local.sh: building $component/$project ($CONFIGURATION, log $(rel "$LOGDIR/$log"))"
    # The job writes the log through the descriptor open_log created; neither it nor the script keeps
    # another copy of that descriptor.
    (cd "$REPO_ROOT/$component" && exec setsid -w dotnet build "$project" -c "$CONFIGURATION" "$@") \
        >&"$logfd" 2>&1 {logfd}>&- &
    pid=$!
    exec {logfd}>&-
    supervise "$pid" "the $component build of $project"
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
# blocks the next run, whose await_listeners names it instead. Its log, artifacts/local/<name>.log, is
# made by open_log, and the process writes it through the descriptor open_log created alone. Readiness
# is the harness's: it polls each /health within its readiness limit.
start_bg() {
    local name="$1" component="$2" dll="$3" logfd pid
    shift 3
    open_log "$name.log"
    logfd="$LOG_OUT"
    (
        cd "$REPO_ROOT/$component" || exit 1
        exec env "${PROC_ENV[@]}" dotnet "$dll" "$@"
    ) >&"$logfd" 2>&1 9>&- {logfd}>&- &
    pid=$!
    exec {logfd}>&-
    PIDS+=("$pid")
    NAMES[$pid]="$name"
    STARTED_PID="$pid"
    echo "started $name pid $pid (log $(rel "$LOGDIR/$name.log"))"
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
            quote_tail 20 "$name.log"
            exit 1
        fi
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "run-local.sh: $name pid $pid exited during start-up; the harness is not run; last lines of $(rel "$log"):" >&2
            quote_tail 20 "$name.log"
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
        quote_tail 20 "$name.log"
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

# cleanup: stops, once, the supervised build or facade-test job in flight and reaps its logger, removes
# the facade tests' staging directory when one is left (remove_staging), then stops the harness if it is
# still running and reaps its logger, then every started process in reverse start order (the facade
# before the stand-in). A logger pid is dropped only once close_tee has seen that logger gone.
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
    if [ -n "$STAGE_NAME" ]; then
        remove_staging
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
# tee writes the log through its inherited copy of the descriptor open_log created (/dev/fd/<n>), never
# through the log's path, and the script closes its own copy before the harness starts.
run_harness() {
    local mode="$1" log logfd
    log="$LOGDIR/harness-$mode.log"
    open_log "harness-$mode.log"
    logfd="$LOG_OUT"
    HARNESS_LOG="$log"
    exec 3> >(trap '' INT TERM HUP; exec tee "/dev/fd/$logfd")
    TEE_PID=$!
    exec {logfd}>&-
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

# clear_facade_results: removes artifacts/facade-tests/ and checks that nothing of it is left. rm runs
# inside the held artifacts/, entered through its descriptor and confirmed at its physical path, and is
# given the relative name alone: it removes a symbolic link there itself and follows none inside the
# tree, so nothing outside artifacts/ is removed.
clear_facade_results() {
    local results="$ART/facade-tests" entry="/proc/$$/fd/$ART_FD/facade-tests"
    if ! (cd -P -- "/proc/$$/fd/$ART_FD" && [ "$(pwd -P)" = "$ART" ] && rm -rf -- facade-tests) ||
        [ -e "$entry" ] || [ -L "$entry" ]; then
        refuse_evidence "cannot remove $(rel "$results")/"
    fi
}

# publish_status <status>: publishes <status> as exit-code in the held artifacts/facade-tests/. It is
# written in full under a fresh temporary name and then renamed over exit-code (create_temp,
# publish_temp), so a reader never sees a partial or an earlier status and a symbolic link put at
# exit-code is replaced, never followed. False when the status was not published.
publish_status() {
    create_temp "$RESULTS_FD" exit-code || return 1
    if ! printf '%s\n' "$1" >&"$TEMP_FD"; then
        exec {TEMP_FD}>&-
        rm -f -- "/proc/$$/fd/$RESULTS_FD/$TEMP_NAME"
        return 1
    fi
    exec {TEMP_FD}>&-
    publish_temp "$RESULTS_FD" exit-code
}

# make_staging: creates the facade tests' private staging directory in the held artifacts/ under the
# fresh name .facade-tests.<random_name_part>, holds it open as STAGE_FD (hold_dir) and sets STAGE_NAME.
# mkdir -m 0700 follows no symbolic link and fails on any entry already at that name. The directory is
# kept only when stat, through the held descriptor, shows mode 700 and the effective user as its owner;
# otherwise the run ends with 1 before the harness starts (refuse_evidence), and cleanup removes it with
# rmdir alone (remove_staging).
make_staging() {
    local part mode_owner
    if ! part="$(random_name_part)" ||
        ! mkdir -m 0700 -- "/proc/$$/fd/$ART_FD/.facade-tests.$part" 2>/dev/null; then
        refuse_evidence "cannot create a private staging directory for the facade tests in $(rel "$ART")/"
    fi
    STAGE_NAME=".facade-tests.$part"
    hold_dir "$ART_FD" "$ART/$STAGE_NAME"
    STAGE_FD="$HELD_FD"
    mode_owner="$(stat -L -c '%a %u' -- "/proc/$$/fd/$STAGE_FD" 2>/dev/null)"
    if [ "$mode_owner" != "700 $EUID" ]; then
        refuse_evidence "the staging directory $(rel "$ART/$STAGE_NAME")/ is not private to this run (mode and owner ${mode_owner:-unreadable}, not 700 $EUID)"
    fi
    STAGE_PRIVATE=1
}

# remove_staging: closes STAGE_FD and removes the staging directory from the held artifacts/. Once it
# has passed its check, rm -rf is given its relative name inside artifacts/, entered through ART_FD as
# in clear_facade_results, so it follows no symbolic link in the tree; before that only rmdir, which
# removes no symbolic link and no non-empty directory, so nothing that may not be this run's is removed.
# Warns when an entry is left at that name.
remove_staging() {
    local entry
    if [ -z "$STAGE_NAME" ]; then
        return 0
    fi
    if [ -n "$STAGE_FD" ]; then
        exec {STAGE_FD}<&-
        STAGE_FD=""
    fi
    entry="/proc/$$/fd/$ART_FD/$STAGE_NAME"
    if [ "$STAGE_PRIVATE" -eq 1 ]; then
        (cd -P -- "/proc/$$/fd/$ART_FD" && [ "$(pwd -P)" = "$ART" ] && rm -rf -- "$STAGE_NAME")
    else
        rmdir -- "$entry" 2>/dev/null
    fi
    if [ -e "$entry" ] || [ -L "$entry" ]; then
        echo "run-local.sh: warning: the staging directory $(rel "$ART/$STAGE_NAME")/ was not removed" >&2
    fi
    STAGE_NAME=""
    STAGE_PRIVATE=0
}

# publish_result <name>: publishes the file <name> the facade tests wrote into the staging directory by
# renaming it into the held artifacts/facade-tests/. mv -fT renames with rename(2) between the two held
# directories, both made by this run in artifacts/ and so on one filesystem: nothing is opened or
# copied, and a file or symbolic link put at <name> in artifacts/facade-tests/ is replaced without
# being followed. A staged entry that is a symbolic link or not a regular file is refused and left to
# remove_staging, never read, followed or moved. The published entry is checked again, so one replaced
# in the staging directory between the check and the rename is unlinked (rm -f, which removes no
# directory) and reported rather than published. False when <name> was not published, with an error
# unless the tests wrote no <name> at all.
publish_result() {
    local results="$ART/facade-tests" staged="/proc/$$/fd/$STAGE_FD/$1" published="/proc/$$/fd/$RESULTS_FD/$1"
    if [ ! -e "$staged" ] && [ ! -L "$staged" ]; then
        return 1
    fi
    if [ -L "$staged" ] || [ ! -f "$staged" ]; then
        echo "run-local.sh: error: the facade tests wrote $1 as a symbolic link or a non-regular file;" \
            "not published to $(rel "$results")/" >&2
        return 1
    fi
    if ! mv -fT -- "$staged" "$published" 2>/dev/null; then
        echo "run-local.sh: error: cannot rename the facade tests' $1 into $(rel "$results")/; not published" >&2
        return 1
    fi
    if [ -L "$published" ] || [ ! -f "$published" ]; then
        rm -f -- "$published" 2>/dev/null
        echo "run-local.sh: error: the facade tests' $1 was replaced by a symbolic link or a non-regular" \
            "file before it reached $(rel "$results")/; not published" >&2
        return 1
    fi
}

# run_facade_tests: the facade tests as facade/tests/run-tests.sh runs them, into a cleared
# artifacts/facade-tests/, so an old TRX is never read as this run's evidence. The directory is made
# afresh by a mkdir inside the held artifacts/, which fails on any entry that has reappeared, and is
# then held open itself (hold_dir). The tests write into a private staging directory (make_staging),
# never into artifacts/facade-tests/. The run is a supervised job bounded by FACADE_TESTS_SECONDS; at
# the limit it is stopped and its status is 124. Its TRX and xunit XML are then published from the
# staging directory (publish_result), and only after them its status as exit-code (publish_status), so
# a reader that sees this run's status also sees this run's TRX; the staging directory is then removed
# (remove_staging). The run continues whatever the status and whether or not a TRX was published; the
# harness reports the outcome.
run_facade_tests() {
    local results="$ART/facade-tests" log="$LOGDIR/facade-tests.log" logfd status trx_published=0 xml_published=0
    clear_facade_results
    if ! mkdir -- "/proc/$$/fd/$ART_FD/facade-tests" 2>/dev/null; then
        refuse_evidence "cannot create $(rel "$results")/"
    fi
    hold_dir "$ART_FD" "$results"
    RESULTS_FD="$HELD_FD"
    make_staging
    open_log facade-tests.log
    logfd="$LOG_OUT"
    echo "run-local.sh: running the facade tests (results $(rel "$results")/, log $(rel "$log")," \
        "limit $FACADE_TESTS_SECONDS s)"
    # The console copy goes through a logger the script tracks, as for the harness; tee ignores the
    # signals so both copies stay complete while cleanup stops the tests. tee writes the log through its
    # inherited copy of the descriptor open_log created, and the script closes its own copy.
    exec 4> >(trap '' INT TERM HUP; exec tee "/dev/fd/$logfd")
    TEST_TEE_PID=$!
    exec {logfd}>&-
    # No `--` before the test-application options: on SDK 10.0.403 `dotnet test` rejects
    # `-- ... --results-directory <dir>` when <dir> exists, and writes no TRX. The results directory is
    # given as the /proc path of the held staging directory, so the TRX and xunit XML land in the
    # directory make_staging checked whatever happens to its path meanwhile.
    (cd "$REPO_ROOT/facade" && exec setsid -w dotnet test \
        --project tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj -c "$CONFIGURATION" --no-restore \
        --report-xunit-trx --report-xunit-trx-filename facade-tests.trx \
        --report-xunit-xml --report-xunit-xml-filename facade-tests.xunit.xml \
        --results-directory "/proc/$$/fd/$STAGE_FD") \
        >&4 2>&1 4>&- &
    supervise "$!" "the facade tests" "$FACADE_TESTS_SECONDS"
    status=$?
    # A logger still running after SIGKILL keeps its pid in TEST_TEE_PID, so cleanup tries again.
    close_tee 4 "$TEST_TEE_PID" "$log" "the facade tests'"
    [ "$?" -eq 2 ] || TEST_TEE_PID=""
    if publish_result facade-tests.trx; then
        trx_published=1
    fi
    if publish_result facade-tests.xunit.xml; then
        xml_published=1
    fi
    if ! publish_status "$status"; then
        refuse_evidence "cannot write $(rel "$results")/exit-code (the facade tests exited $status)"
    fi
    echo "run-local.sh: facade tests exited $status"
    if [ "$trx_published" -eq 0 ]; then
        echo "run-local.sh: warning: $(rel "$results")/facade-tests.trx was not written" >&2
    fi
    if [ "$xml_published" -eq 0 ]; then
        echo "run-local.sh: warning: $(rel "$results")/facade-tests.xunit.xml was not written" >&2
    fi
    remove_staging
    exec {RESULTS_FD}<&-
    RESULTS_FD=""
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

# open_lock: opens artifacts/local/run-local.lock, inside the held artifacts/local/, as fd 9. The file is
# never unlinked or replaced, since a run that holds the lock holds that file. A symbolic link there, or
# any other entry that is not a regular file, is refused (refuse_output). An absent one is created under
# noclobber, O_CREAT|O_EXCL, which never follows a symbolic link; when another run creates it first, that
# one is opened instead. An existing one is opened read-only, which is all flock needs: unlike `>>` or
# `<>` that open has no O_CREAT, so a dangling link put there after the check creates nothing elsewhere,
# and the check before it keeps the open off a FIFO, on which it would block. fd 9 is kept only when
# /proc shows a regular file at artifacts/local/run-local.lock.
open_lock() {
    local -
    local lock="$LOGDIR/run-local.lock" entry="/proc/$$/fd/$LOG_FD/run-local.lock" opened=0
    if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then
        set -C
        { exec 9>"$entry"; } 2>/dev/null && opened=1
    fi
    if [ "$opened" -eq 0 ]; then
        if [ -L "$entry" ]; then
            refuse_output "$lock" "a symbolic link"
        elif [ -e "$entry" ] && [ ! -f "$entry" ]; then
            refuse_output "$lock" "not a regular file"
        elif ! { exec 9<"$entry"; } 2>/dev/null; then
            echo "run-local.sh: cannot open $(rel "$lock")" >&2
            exit 1
        fi
    fi
    if [ ! -f "/proc/$$/fd/9" ] || [ "$(readlink -- "/proc/$$/fd/9")" != "$lock" ]; then
        exec 9<&-
        refuse_output "$lock" "no longer the regular file it was checked to be"
    fi
}

# prepare: the checks and directories every subcommand needs, once its name is known to be valid. The
# programs the build, start, supervision, logging and output steps run are looked up on PATH first, so
# a missing one stops the run with its name before anything is built, started or written under
# artifacts/. Root resolution has run before this check and needs dirname, and readlink when the script
# is invoked through a symbolic link; without them it ends the run with "cannot locate the repository".
# Unchecked, a missing setsid would leave builds and tests unsupervised, a missing ss or grep would make
# every port-ownership check fail, a missing flock would let a second run into this checkout, a missing
# tee would lose the logs and the harness's console copy, a missing mv or readlink (or od or tr, where
# bash has no SRANDOM) would make every output under artifacts/ fail, a missing stat would refuse every
# staging directory of the facade tests, and a missing rmdir would leave one that failed its check
# behind. artifacts/ and artifacts/local/ are then held open (hold_dir) and the checkout lock is taken
# (open_lock).
prepare() {
    local tool missing=()
    local -a tools=(dotnet setsid ss flock grep tee env sed tail sleep mkdir rm rmdir mv readlink stat basename)
    if ! has_srandom; then
        tools+=(od tr)
    fi
    for tool in "${tools[@]}"; do
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
    if [ ! -d "/proc/$$/fd" ]; then
        echo "run-local.sh: /proc/$$/fd is not available, so artifacts/ cannot be held open; this run writes nothing under it" >&2
        exit 1
    fi
    # The repository root is held only while artifacts/ is opened in it.
    if ! { exec {ROOT_FD}<"$REPO_ROOT/."; } 2>/dev/null ||
        [ "$(readlink -- "/proc/$$/fd/$ROOT_FD")" != "$REPO_ROOT" ]; then
        echo "run-local.sh: cannot hold the repository $REPO_ROOT open; this run writes nothing under it" >&2
        exit 1
    fi
    hold_dir "$ROOT_FD" "$ART"
    ART_FD="$HELD_FD"
    exec {ROOT_FD}<&-
    ROOT_FD=""
    hold_dir "$ART_FD" "$LOGDIR"
    LOG_FD="$HELD_FD"
    # One run per checkout: fd 9 holds an exclusive lock on artifacts/local/run-local.lock until the
    # script exits, so a second run here stops before it builds or writes anything. The lock is not
    # host-wide; await_listeners tells apart runs of other checkouts. The stand-in, the facade and the
    # harness are started without fd 9, so one left running by a SIGKILLed run never holds the lock and
    # the next run's await_listeners names it instead.
    open_lock
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
