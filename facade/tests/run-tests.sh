#!/usr/bin/env bash
# Entrypoint of the Compose service facade-tests (facade/Dockerfile stage `test`: run from /src/facade,
# no network, repository mounted at /repo). harness/scripts/run-local.sh runs the facade tests the same
# way from a checkout, and this script works from one as well.
#
# Runs the routing, failure-behaviour and listener-isolation tests and leaves three files in the results
# directory for harness/src/Erewhon.Harness/Checks/FacadeTestResults.cs: facade-tests.trx (VS TeamTest
# TRX), facade-tests.xunit.xml (xunit XML, the only record of each failure's exception type) and
# exit-code (the status of the last dotnet command). The script always exits 0, because the harness
# service depends on this one with condition service_completed_successfully; the real outcome travels
# in those files.
#
# Before any dotnet command the previous run's three files are removed and must be gone, and exit-code
# is created afresh holding the non-integer marker `running`, which the harness reads as unverified until
# the runner status replaces it. When the old files survive or exit-code cannot be created, nothing runs
# and status 1 is recorded wherever exit-code is still writable. Only what this run creates or writes
# is made writable, so a later local run can replace the results of a root-run container: a results
# directory this run creates gets that mode as it is created, exit-code and this run's TRX and xunit XML
# get it once written, and an existing results directory keeps its owner's mode.
#
# Usage: run-tests.sh [-h | --help]. Any other argument is reported and nothing runs.
#
# The test run is bounded, so a hung test host cannot stall the harness: past a 600 s suite limit the
# run and every process it started are stopped (SIGTERM, then SIGKILL 10 s later), and exit-code then
# holds a nonzero status. This needs setsid (util-linux).
#
# Environment:
#   FACADE_TEST_RESULTS_DIR  results directory; default /repo/artifacts/facade-tests. A relative path is
#                            taken from the caller's working directory. It must be a dedicated
#                            directory: /, the facade directory or any ancestor of it, and the root of a
#                            git work tree are refused and left untouched.

set -u
set -o pipefail
# No `set -e`: a failed test run must still reach the exit-code write and `exit 0`.
# A CDPATH match would make `cd` print a path into the $(cd ... && pwd) resolutions below.
unset CDPATH

TEST_PROJECT="tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj"
TRX_NAME="facade-tests.trx"
XML_NAME="facade-tests.xunit.xml"
EXIT_CODE_NAME="exit-code"
DEFAULT_RESULTS_DIR="/repo/artifacts/facade-tests"
# exit-code's content while a run is in progress. FacadeTestResults.cs reads a non-integer as an
# unverified run, so an interrupted run can never pass for a successful one.
RUNNING_MARKER="running"
# The suite limit bounds the whole dotnet test run: the build check, the test host and the TRX write.
# No test or request timeout reaches a test host that blocks outside a request (in a host start-up or
# disposal, or in the runner itself), and the harness waits for this script to end. A healthy run
# takes well under a minute; the limit also leaves room for a first build of a fresh checkout.
readonly SUITE_LIMIT_SECONDS=600
# Past the limit the run gets SIGTERM and this long to end on it before SIGKILL ends whatever is left of
# it. A run stopped either way writes no TRX or xunit XML, so exit-code alone records its outcome.
readonly KILL_GRACE_SECONDS=10

usage() {
    cat <<EOF
Usage: $0 [-h | --help]

Runs the facade's routing, failure-behaviour and listener-isolation tests from the facade directory
and leaves three files in the results directory for the equivalence harness:
  $TRX_NAME        TRX of the test run
  $XML_NAME  xunit XML of the same run, recording each failure's exception type
  $EXIT_CODE_NAME               status of the last dotnet command ($RUNNING_MARKER while the run is in progress)

Environment:
  FACADE_TEST_RESULTS_DIR  results directory; default $DEFAULT_RESULTS_DIR. A relative path is
                           taken from the caller's working directory. It must be a dedicated
                           directory: /, the facade directory or any ancestor of it, and the root of
                           a git work tree are refused and left untouched.

The script always exits 0; the test outcome travels in $EXIT_CODE_NAME and $TRX_NAME.
EOF
}

# Arguments are checked before anything touches the filesystem, so neither a help request nor a typo
# clears the results directory or runs the tests. Unknown arguments still exit 0, like every other path.
show_help=0
for arg in "$@"; do
    case "$arg" in
        -h | --help) show_help=1 ;;
        *)
            echo "facade-tests: unknown argument: $arg; tests not run" >&2
            usage >&2
            exit 0
            ;;
    esac
done
if [ "$show_help" -eq 1 ]; then
    usage
    exit 0
fi

# dotnet finds global.json by searching upward from the working directory, not from the project path,
# so every dotnet command runs from facade/: that is what makes facade/global.json select SDK 10.0.403
# and the Microsoft.Testing.Platform runner. The directory is resolved here, because the results
# directory is checked against it, and entered only once the results are set up, so that a failure to
# enter it is still recorded in exit-code.
FACADE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
FACADE_PHYSICAL_DIR=""
if [ -n "$FACADE_DIR" ]; then
    FACADE_PHYSICAL_DIR="$(cd -- "$FACADE_DIR" && pwd -P)"
fi

RESULTS_DIR="${FACADE_TEST_RESULTS_DIR:-$DEFAULT_RESULTS_DIR}"
# The container runs as root, so only a results directory this run's own mkdir creates is made
# writable for later local runs. It gets that mode from the creating call itself: under umask 0, GNU
# mkdir (the image's) creates it 0777 in its one mkdir(2). The script never chmods the directory's
# path afterwards, so nothing put in its place later can have its mode changed. That mkdir fails with
# EEXIST for anything already there (a directory in any spelling, one ending in `..` or `.` included,
# or a symlink to anything), which keeps the mode its owner set. Intermediate directories are created
# with the caller's umask.
mkdir -p -- "$(dirname -- "$RESULTS_DIR")"
(umask 0 && mkdir -- "$RESULTS_DIR") 2>/dev/null
# The physical path is the one checked below, so a symlink cannot disguise a refused directory.
if [ ! -d "$RESULTS_DIR" ] || ! RESULTS_PHYSICAL_DIR="$(cd -- "$RESULTS_DIR" && pwd -P)"; then
    echo "facade-tests: cannot create results directory $RESULTS_DIR; tests not run" >&2
    exit 0
fi
RESULTS_DIR="$RESULTS_PHYSICAL_DIR"

# The script removes, writes and shares files in the results directory, so an override naming a
# directory that holds other work is refused before anything in it changes: the filesystem root, the
# facade directory or any ancestor of it (a checkout root and above), and the root of a git work tree
# (/repo in the container).
refusal=""
if [ "$RESULTS_DIR" = "/" ]; then
    refusal="it is the filesystem root"
elif [ -n "$FACADE_PHYSICAL_DIR" ] && [[ "$FACADE_PHYSICAL_DIR/" == "$RESULTS_DIR/"* ]]; then
    refusal="it is the facade directory or one of its ancestors"
elif [ -e "$RESULTS_DIR/.git" ] || [ -L "$RESULTS_DIR/.git" ]; then
    refusal="it is the root of a git work tree"
fi
if [ -n "$refusal" ]; then
    echo "facade-tests: refusing results directory $RESULTS_DIR: $refusal; FACADE_TEST_RESULTS_DIR must name a dedicated directory; nothing in it was changed; tests not run" >&2
    exit 0
fi

TRX_PATH="$RESULTS_DIR/$TRX_NAME"
XML_PATH="$RESULTS_DIR/$XML_NAME"
EXIT_CODE_PATH="$RESULTS_DIR/$EXIT_CODE_NAME"

# share <file>: leaves a file this run wrote writable for later local runs, because the container runs
# as root. A symlink is never followed, so nothing outside the results changes mode.
share() {
    if [ -f "$1" ] && [ ! -L "$1" ]; then
        chmod a+rw -- "$1" 2>/dev/null || true
    fi
}

# write_status <status>: puts <status> in exit-code, only where that writes no symlink and nothing but
# a plain file: exit-code is absent, or a regular, writable file. False when the status was not written.
write_status() {
    if [ -L "$EXIT_CODE_PATH" ]; then
        return 1
    fi
    if [ -e "$EXIT_CODE_PATH" ] && { [ ! -f "$EXIT_CODE_PATH" ] || [ ! -w "$EXIT_CODE_PATH" ]; }; then
        return 1
    fi
    { printf '%s\n' "$1" >"$EXIT_CODE_PATH"; } 2>/dev/null || return 1
    share "$EXIT_CODE_PATH"
}

# record <status>: replaces the marker in exit-code with the runner status. Should that fail, the
# marker stays, and the harness reports the results as unverified instead of trusting them.
record() {
    if ! write_status "$1"; then
        echo "facade-tests: error: could not record runner status $1 in $EXIT_CODE_PATH; the harness will report the results in $RESULTS_DIR as unverified" >&2
    fi
}

# A stale TRX, xunit XML or status must never be read as this run's evidence. All three are removed and
# must then be gone (a directory or a dangling symlink in their place counts as present), and exit-code
# is created afresh holding the marker; noclobber keeps that write from going through a file or symlink
# that appeared in between. Until all of that holds, no dotnet command runs.
rm -f -- "$TRX_PATH" "$XML_PATH" "$EXIT_CODE_PATH" 2>/dev/null
fresh_failure=""
if [ -e "$TRX_PATH" ] || [ -L "$TRX_PATH" ] || [ -e "$XML_PATH" ] || [ -L "$XML_PATH" ] ||
    [ -e "$EXIT_CODE_PATH" ] || [ -L "$EXIT_CODE_PATH" ]; then
    fresh_failure="stale evidence could not be cleared from $RESULTS_DIR"
elif ! (set -o noclobber && printf '%s\n' "$RUNNING_MARKER" >"$EXIT_CODE_PATH") 2>/dev/null; then
    fresh_failure="$RESULTS_DIR is not writable"
fi
if [ -n "$fresh_failure" ]; then
    # A stale passing status is replaced by 1 wherever exit-code can still be written.
    if write_status 1; then
        status_note="status 1 recorded in $EXIT_CODE_PATH"
    else
        status_note="no status could be recorded"
    fi
    echo "facade-tests: error: $fresh_failure; the results in $RESULTS_DIR are not from this run; $status_note; tests not run" >&2
    exit 0
fi
share "$EXIT_CODE_PATH"

if [ -z "$FACADE_DIR" ] || ! cd -- "$FACADE_DIR"; then
    record 1
    echo "facade-tests: cannot enter the facade directory of $0; tests not run" >&2
    exit 0
fi

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# No MSBuild node, MSBuild server or compiler server outlives the run or is shared with other builds
# on the host; a caller's own settings are kept.
export MSBUILDDISABLENODEREUSE="${MSBUILDDISABLENODEREUSE:-1}"
export DOTNET_CLI_USE_MSBUILD_SERVER="${DOTNET_CLI_USE_MSBUILD_SERVER:-0}"
export UseSharedCompilation="${UseSharedCompilation:-false}"

# In the image the build stage has already restored, so this is skipped and nothing touches the
# network. A fresh checkout restores here, against the committed lock files.
if [ ! -f tests/Erewhon.Facade.Tests/obj/project.assets.json ]; then
    dotnet restore --locked-mode "$TEST_PROJECT"
    rc=$?
    if [ "$rc" -ne 0 ]; then
        record "$rc"
        echo "facade-tests: dotnet restore --locked-mode exited $rc; tests not run; results in $RESULTS_DIR" >&2
        exit 0
    fi
fi

if ! command -v setsid >/dev/null 2>&1; then
    record 127
    echo "facade-tests: setsid (util-linux) is not on PATH, so the test run cannot be bounded; tests not run" >&2
    exit 0
fi

# run_bounded <command> [<argument>...]: runs the command in a session and process group of its own and
# sets rc to its exit status. Past SUITE_LIMIT_SECONDS the whole group gets SIGTERM, then SIGKILL from
# KILL_GRACE_SECONDS later for as long as any of it is left, so neither the dotnet CLI nor the test host
# it starts outlives the run, even when stopped or ignoring SIGTERM; STOPPED_BY is then "limit". The
# group is signalled from here rather than through timeout(1), because the uutils timeout's
# `--kill-after` never sends SIGKILL to a command that ignores SIGTERM. The run does not receive this
# script's terminal signals, so a TERM, INT or HUP sent to the script stops it the same way, and
# STOPPED_BY is then "signal".
run_bounded() {
    local pid term_at kill_at="" stop_requested=0
    STOPPED_BY=""
    trap 'stop_requested=1' TERM INT HUP
    setsid "$@" &
    pid=$!
    term_at=$((SECONDS + SUITE_LIMIT_SECONDS))
    # The run is over once the command has ended and no process is left in its group. The job table says
    # whether the command still runs, so a reused pid is never taken for it.
    while [ -n "$(jobs -rp)" ] || kill -0 -- "-$pid" 2>/dev/null; do
        if [ -z "$kill_at" ]; then
            if [ "$stop_requested" -eq 1 ]; then
                STOPPED_BY="signal"
            elif [ "$SECONDS" -ge "$term_at" ]; then
                STOPPED_BY="limit"
            fi
            if [ -n "$STOPPED_BY" ]; then
                kill -TERM -- "-$pid" 2>/dev/null
                kill_at=$((SECONDS + KILL_GRACE_SECONDS))
            fi
        elif [ "$SECONDS" -ge "$kill_at" ]; then
            kill -KILL -- "-$pid" 2>/dev/null
        fi
        sleep 1
    done
    wait "$pid"
    rc=$?
    trap - TERM INT HUP
}

# The xunit.v3 options are given without a `--` separator. On SDK 10.0.403, `dotnet test` in
# Microsoft.Testing.Platform mode rejects `-- ... --results-directory <dir>` when <dir> exists (it
# exits 1 with "Specifying a directory for 'dotnet test' should be via '--project' or '--solution'."
# and writes no TRX), and <dir> always exists here; without the separator the options still reach
# the test application.
#
# On the .NET 8 fallback (KL-SUB-DOTNET, VSTest runner) the run line becomes:
#   run_bounded dotnet test tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=facade-tests.trx" --results-directory "$RESULTS_DIR"
run_bounded dotnet test --project "$TEST_PROJECT" -c Release --no-restore \
    --report-xunit-trx --report-xunit-trx-filename "$TRX_NAME" \
    --report-xunit-xml --report-xunit-xml-filename "$XML_NAME" --results-directory "$RESULTS_DIR"
case "$STOPPED_BY" in
    limit)
        echo "facade-tests: dotnet test exceeded the ${SUITE_LIMIT_SECONDS} s suite limit and was stopped" >&2
        ;;
    signal)
        echo "facade-tests: dotnet test was stopped on a signal to this script" >&2
        ;;
esac
# A stopped run never counts as passed, whatever status it ended with.
if [ -n "$STOPPED_BY" ] && [ "$rc" -eq 0 ]; then
    rc=124
fi

record "$rc"
# The stale TRX and xunit XML were removed before the run, so each one here, or its absence, is this run's.
share "$TRX_PATH"
share "$XML_PATH"
echo "facade-tests: dotnet test exited $rc; results in $RESULTS_DIR"
if [ ! -f "$TRX_PATH" ]; then
    echo "facade-tests: warning: $TRX_PATH was not written; the harness reports CHK-FACADE-TRX missing" >&2
fi
if [ ! -f "$XML_PATH" ]; then
    echo "facade-tests: warning: $XML_PATH was not written; the harness cannot record failed tests' exception types" >&2
fi
exit 0
