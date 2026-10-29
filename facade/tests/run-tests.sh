#!/usr/bin/env bash
# Entrypoint of the Compose service facade-tests (facade/Dockerfile stage `test`: run from /src/facade,
# no network, repository mounted at /repo). harness/scripts/run-local.sh runs the facade tests the same
# way from a checkout, and this script works from one as well.
#
# Runs the routing, failure-behaviour and listener-isolation tests and leaves two files in the results
# directory for harness/src/Erewhon.Harness/Checks/FacadeTestResults.cs: facade-tests.trx (VS TeamTest
# TRX) and exit-code (the status of the last dotnet command). The script always exits 0, because the
# harness service depends on this one with condition service_completed_successfully; the real outcome
# travels in those two files.
#
# Environment:
#   FACADE_TEST_RESULTS_DIR  results directory; default /repo/artifacts/facade-tests. A relative path is
#                            taken from the caller's working directory.

set -u
set -o pipefail
# No `set -e`: a failed test run must still reach the exit-code write and `exit 0`.
# A CDPATH match would make `cd` print a path into the $(cd ... && pwd) resolutions below.
unset CDPATH

TEST_PROJECT="tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj"
TRX_NAME="facade-tests.trx"
EXIT_CODE_NAME="exit-code"

RESULTS_DIR="${FACADE_TEST_RESULTS_DIR:-/repo/artifacts/facade-tests}"
if ! mkdir -p -- "$RESULTS_DIR" || ! RESULTS_DIR="$(cd -- "$RESULTS_DIR" && pwd)"; then
    echo "facade-tests: cannot create results directory $RESULTS_DIR; tests not run" >&2
    exit 0
fi
# A stale TRX or status must never be read as this run's evidence.
rm -f -- "$RESULTS_DIR/$TRX_NAME" "$RESULTS_DIR/$EXIT_CODE_NAME"

# record <status>: writes the exit-code file and leaves the results writable for later local runs,
# because the container runs as root.
record() {
    printf '%s\n' "$1" >"$RESULTS_DIR/$EXIT_CODE_NAME" ||
        echo "facade-tests: warning: could not write $RESULTS_DIR/$EXIT_CODE_NAME" >&2
    chmod -R a+rwX "$RESULTS_DIR" 2>/dev/null || true
}

# dotnet finds global.json by searching upward from the working directory, not from the project path,
# so every dotnet command runs from facade/: that is what makes facade/global.json select SDK 10.0.403
# and the Microsoft.Testing.Platform runner.
FACADE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
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

# The xunit.v3 options are given without a `--` separator. On SDK 10.0.403, `dotnet test` in
# Microsoft.Testing.Platform mode rejects `-- ... --results-directory <dir>` when <dir> exists (it
# exits 1 with "Specifying a directory for 'dotnet test' should be via '--project' or '--solution'."
# and writes no TRX), and <dir> always exists here; without the separator the options still reach
# the test application.
#
# On the .NET 8 fallback (KL-SUB-DOTNET, VSTest runner) the run line becomes:
#   dotnet test tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=facade-tests.trx" --results-directory "$RESULTS_DIR"
dotnet test --project "$TEST_PROJECT" -c Release --no-restore \
    --report-xunit-trx --report-xunit-trx-filename "$TRX_NAME" --results-directory "$RESULTS_DIR"
rc=$?

record "$rc"
echo "facade-tests: dotnet test exited $rc; results in $RESULTS_DIR"
if [ ! -f "$RESULTS_DIR/$TRX_NAME" ]; then
    echo "facade-tests: warning: $RESULTS_DIR/$TRX_NAME was not written; the harness reports CHK-FACADE-TRX missing" >&2
fi
exit 0
