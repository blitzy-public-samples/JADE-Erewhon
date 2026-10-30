#!/usr/bin/env bash
# Entrypoint of the Compose service facade-tests (facade/Dockerfile stage `test`: run from /src/facade,
# no network, repository mounted at /repo). harness/scripts/run-local.sh runs the facade tests the same
# way from a checkout, and this script works from one as well.
#
# Runs the routing, failure-behaviour and listener-isolation tests and leaves three files in the results
# directory for harness/src/Erewhon.Harness/Checks/FacadeTestResults.cs: facade-tests.trx (VS TeamTest
# TRX), facade-tests.xunit.xml (xunit XML, the only record of each failure's exception type) and
# exit-code (the status of the last dotnet command, holding `running` while a run is in progress). The
# script always exits 0, because the harness service depends on this one with condition
# service_completed_successfully; the real outcome travels in those files. Once the results directory
# is accepted, a run that cannot start records a nonzero status in exit-code instead, wherever exit-code
# can still be published. dotnet test writes into a private staging directory, and everything reaches
# the results directory only through publish (below).
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
#   TMPDIR                   where the staging directory is made; default /tmp. It and every directory
#                            above it must be owned by root or the effective user and be sticky or
#                            writable by neither group nor others, or nothing runs and exit-code holds 1.

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
  TMPDIR                   where the staging directory is made; default /tmp. It and every directory
                           above it must be owned by root or the effective user and be sticky or
                           writable by neither group nor others, or nothing runs and $EXIT_CODE_NAME holds 1.

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
# with the caller's umask. Anyone may add, replace or remove entries of a 0777 directory, and that
# reaches nothing outside it: this script only creates fresh names there exclusively and renames them
# (publish, below) or unlinks names, none of which follows a symlink found there.
mkdir -p -- "$(dirname -- "$RESULTS_DIR")"
(umask 0 && mkdir -- "$RESULTS_DIR") 2>/dev/null
# The physical path is the one checked below, so a symlink cannot disguise a refused directory.
if [ ! -d "$RESULTS_DIR" ] || ! RESULTS_PHYSICAL_DIR="$(cd -- "$RESULTS_DIR" && pwd -P)"; then
    echo "facade-tests: cannot create results directory $RESULTS_DIR; tests not run" >&2
    exit 0
fi
RESULTS_DIR="$RESULTS_PHYSICAL_DIR"

# The script removes and publishes files in the results directory, so an override naming a
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

# The directory that passed the checks above is held open from here on, and every publication enters
# it through this descriptor, so each one lands in that directory even if its path is changed later.
# Where the descriptor cannot be opened, publications enter the directory by its physical path.
RESULTS_FD=""
{ exec {RESULTS_FD}<"$RESULTS_DIR"; } 2>/dev/null || RESULTS_FD=""

# enter_results_dir: makes the results directory the working directory, through the held descriptor
# (/proc/<pid>/fd/<n>) where /proc provides it and by its physical path otherwise. False unless the
# directory entered is the checked one, at its checked path.
enter_results_dir() {
    if [ -z "$RESULTS_FD" ] || ! cd -P -- "/proc/$$/fd/$RESULTS_FD" 2>/dev/null; then
        cd -P -- "$RESULTS_DIR" 2>/dev/null || return 1
    fi
    [ "$(pwd -P)" = "$RESULTS_DIR" ]
}

# random_name_part: prints 16 hex digits (64 bits) from the kernel's random number generator, through
# bash's SRANDOM (getrandom(2), bash 5.1 and later) or else /dev/urandom. False when neither yields them.
random_name_part() {
    local part=""
    if [ "${BASH_VERSINFO[0]}" -gt 5 ] || { [ "${BASH_VERSINFO[0]}" -eq 5 ] && [ "${BASH_VERSINFO[1]}" -ge 1 ]; }; then
        printf -v part '%08x%08x' "$SRANDOM" "$SRANDOM"
    else
        part="$(od -An -N8 -tx1 /dev/urandom 2>/dev/null | tr -d ' \n')"
    fi
    [[ "$part" =~ ^[0-9a-f]{16}$ ]] || return 1
    printf '%s\n' "$part"
}

# publish <name> <command> [<argument>...]: makes <name> in the results directory a new regular file,
# mode 0666, holding what the command prints; the only way anything is written there. It works from
# inside the results directory (enter_results_dir) with relative names only, in a subshell, so the
# script's own working directory, umask and options are unchanged:
#   - the temporary name .<name>.<random_name_part> is refused when any entry, a dangling symlink
#     included, is already there;
#   - it is created under noclobber and umask 0, so mode 0666 comes from the creating open itself. For a
#     name with no entry, bash's noclobber open is O_CREAT|O_EXCL, which never follows a symlink and
#     fails on any entry that appears in between. The random part keeps anyone from placing in advance
#     the one kind of entry that noclobber opens without O_EXCL, a non-regular one;
#   - the command writes through the descriptor of that creating open, which is then closed;
#   - mv -fT renames the temporary name over <name>: rename(2) replaces a file or symlink there without
#     following it, and fails on a directory.
# No existing entry is opened for writing, truncated or has its mode changed, so a symlink planted in
# the results directory cannot redirect a write or a mode change of this (in the container, root)
# process to another file. Mode 0666 keeps what a root-run container published writable by a later
# local run. On a failure after the creation the temporary name is unlinked. False when <name> was not
# published.
publish() {
    local name="$1"
    shift
    (
        enter_results_dir || exit 1
        part="$(random_name_part)" || exit 1
        temp=".$name.$part"
        if [ -e "$temp" ] || [ -L "$temp" ]; then
            exit 1
        fi
        umask 0
        set -o noclobber
        exec {out}>"$temp" || exit 1
        if ! "$@" >&"$out"; then
            exec {out}>&-
            rm -f -- "$temp"
            exit 1
        fi
        exec {out}>&-
        if ! mv -fT -- "$temp" "$name"; then
            rm -f -- "$temp"
            exit 1
        fi
    ) 2>/dev/null
}

# write_status <status>: publishes <status> as exit-code, replacing the marker or an earlier status.
# False when the status was not published.
write_status() {
    publish "$EXIT_CODE_NAME" printf '%s\n' "$1"
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
# is published afresh holding the marker; publication replaces, without following, anything that
# appeared at exit-code in between. Until all of that holds, no dotnet command runs. The removal is
# rm -f, an unlink, which never follows a symlink.
rm -f -- "$TRX_PATH" "$XML_PATH" "$EXIT_CODE_PATH" 2>/dev/null
fresh_failure=""
if [ -e "$TRX_PATH" ] || [ -L "$TRX_PATH" ] || [ -e "$XML_PATH" ] || [ -L "$XML_PATH" ] ||
    [ -e "$EXIT_CODE_PATH" ] || [ -L "$EXIT_CODE_PATH" ]; then
    fresh_failure="stale evidence could not be cleared from $RESULTS_DIR"
elif ! publish "$EXIT_CODE_NAME" printf '%s\n' "$RUNNING_MARKER"; then
    fresh_failure="$RESULTS_DIR is not writable"
fi
if [ -n "$fresh_failure" ]; then
    # A stale passing status is replaced by 1 wherever exit-code can still be published.
    if write_status 1; then
        status_note="status 1 recorded in $EXIT_CODE_PATH"
    else
        status_note="no status could be recorded"
    fi
    echo "facade-tests: error: $fresh_failure; the results in $RESULTS_DIR are not from this run; $status_note; tests not run" >&2
    exit 0
fi

# dotnet test writes into a private staging directory, never into the results directory, and
# publish_result publishes what it wrote from there. mktemp -d makes it mode 0700 in the physical
# directory TMPDIR names (default /tmp; a relative TMPDIR is taken from the caller's working
# directory), outside the repository, so its path is absolute and holds no symlink. Another user who can
# rename entries of that directory or of any directory above it could move the staging directory away
# and put a symlink at its path, redirecting this (in the container, root) process's writes and the
# removal below. So, as soon as it is created and before any dotnet command, every directory from the
# physical TMPDIR up to / must be owned by root or the effective user and be either sticky or writable
# by neither group nor others (rename_risk). Only root and the effective user can then change any of
# them, so the checked path keeps naming this run's directory. When one fails the check, the staging
# directory, still empty, is removed by rmdir, which removes no symlink and no non-empty directory,
# status 1 is recorded and nothing runs. Should it not be created, nothing runs either. The EXIT trap
# removes it on every exit of the script, including one on a TERM, INT or HUP outside the test run.
STAGING_DIR=""
STAGING_PRIVATE=0

# remove_staging: removes the staging directory, as the EXIT trap and when its path fails the check:
# with rm -rf once its path has passed the check, and before that only with rmdir, so nothing that may
# belong to someone else is ever removed.
remove_staging() {
    if [ -z "$STAGING_DIR" ]; then
        return 0
    fi
    if [ "$STAGING_PRIVATE" -eq 1 ]; then
        rm -rf -- "$STAGING_DIR"
    else
        rmdir -- "$STAGING_DIR" 2>/dev/null
    fi
}
trap remove_staging EXIT

# rename_risk <directory>: prints why users other than root and the effective user can rename entries
# of <directory>, an absolute physical path, and prints nothing when they cannot: it is a directory, not
# a symlink, owned by root or the effective user, and it is sticky or writable by neither its group nor
# others. stat -c '%a %u' prints the mode in octal with its special digit (1777, 2777) and the owner's
# uid, alike in GNU coreutils and uutils.
rename_risk() {
    local mode_owner mode owner
    if [ -L "$1" ] || [ ! -d "$1" ]; then
        echo "$1 is not a directory"
        return 0
    fi
    mode_owner="$(stat -c '%a %u' -- "$1" 2>/dev/null)"
    mode="${mode_owner%% *}"
    owner="${mode_owner#* }"
    if ! [[ "$mode" =~ ^[0-7]{1,4}$ && "$owner" =~ ^[0-9]+$ ]]; then
        echo "the mode and owner of $1 cannot be read"
    elif [ "$owner" -ne 0 ] && [ "$owner" -ne "$EUID" ]; then
        echo "$1 is owned by uid $owner, neither root nor the effective user"
    elif (((8#$mode & 8#1000) == 0 && (8#$mode & 8#022) != 0)); then
        echo "$1 is group- or world-writable without the sticky bit (mode $mode)"
    fi
}

TMPDIR_PHYSICAL=""
if ! TMPDIR_PHYSICAL="$(cd -P -- "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P)" || [ -z "$TMPDIR_PHYSICAL" ] ||
    ! STAGING_DIR="$(mktemp -d -- "${TMPDIR_PHYSICAL%/}/facade-tests.XXXXXXXXXX" 2>/dev/null)" ||
    [ -z "$STAGING_DIR" ] || [ ! -d "$STAGING_DIR" ]; then
    STAGING_DIR=""
    if write_status 1; then
        status_note="status 1 recorded in $EXIT_CODE_PATH"
    else
        status_note="no status could be recorded"
    fi
    echo "facade-tests: error: cannot create a private staging directory under ${TMPDIR:-/tmp}; $status_note; tests not run" >&2
    exit 0
fi
staging_risk=""
checked_dir="$TMPDIR_PHYSICAL"
while [ -z "$staging_risk" ]; do
    staging_risk="$(rename_risk "$checked_dir")"
    if [ "$checked_dir" = "/" ]; then
        break
    fi
    checked_dir="${checked_dir%/*}"
    checked_dir="${checked_dir:-/}"
done
if [ -n "$staging_risk" ]; then
    remove_staging
    STAGING_DIR=""
    if write_status 1; then
        status_note="status 1 recorded in $EXIT_CODE_PATH"
    else
        status_note="no status could be recorded"
    fi
    echo "facade-tests: error: TMPDIR ${TMPDIR:-/tmp} lets other users rename entries on the staging directory's path: $staging_risk; set TMPDIR to a private directory; $status_note; tests not run" >&2
    exit 0
fi
STAGING_PRIVATE=1

if [ -z "$FACADE_DIR" ] || ! cd -- "$FACADE_DIR"; then
    record 1
    echo "facade-tests: cannot enter the facade directory of $0; tests not run" >&2
    exit 0
fi

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# By default MSBuild node reuse, the MSBuild server and the compiler server are off, so none outlives
# the run or is shared with other host builds; non-empty caller values are kept and can re-enable them.
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
# and writes no TRX), and <dir>, the staging directory, always exists here; without the separator the
# options still reach the test application.
#
# On the .NET 8 fallback (KL-SUB-DOTNET, VSTest runner) the run line becomes:
#   run_bounded dotnet test tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=facade-tests.trx" --results-directory "$STAGING_DIR"
run_bounded dotnet test --project "$TEST_PROJECT" -c Release --no-restore \
    --report-xunit-trx --report-xunit-trx-filename "$TRX_NAME" \
    --report-xunit-xml --report-xunit-xml-filename "$XML_NAME" --results-directory "$STAGING_DIR"
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

# publish_result <name>: publishes the file <name> that dotnet test wrote into the staging directory,
# when it wrote one there as a regular file, and warns when that publication fails. False when <name>
# was not published.
publish_result() {
    local staged="$STAGING_DIR/$1"
    if [ ! -f "$staged" ] || [ -L "$staged" ]; then
        return 1
    fi
    if ! publish "$1" cat -- "$staged"; then
        echo "facade-tests: warning: dotnet test wrote $1, but it could not be published to $RESULTS_DIR/$1" >&2
        return 1
    fi
}

# The staging directory is this run's own, so each file in it, or its absence, is this run's. The TRX
# and xunit XML are published before the status, so a reader that sees this run's status also sees
# this run's TRX.
trx_published=0
xml_published=0
if publish_result "$TRX_NAME"; then
    trx_published=1
fi
if publish_result "$XML_NAME"; then
    xml_published=1
fi
record "$rc"
echo "facade-tests: dotnet test exited $rc; results in $RESULTS_DIR"
if [ "$trx_published" -eq 0 ]; then
    echo "facade-tests: warning: $TRX_PATH was not written; the harness reports CHK-FACADE-TRX missing" >&2
fi
if [ "$xml_published" -eq 0 ]; then
    echo "facade-tests: warning: $XML_PATH was not written; the harness cannot record failed tests' exception types" >&2
fi
exit 0
