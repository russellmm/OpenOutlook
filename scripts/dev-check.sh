#!/usr/bin/env bash
# Fast pre-commit check: build the solution and run the offline test suite.
#
#   scripts/dev-check.sh              # build + test
#   scripts/dev-check.sh --no-tests   # build only
#
# Notes
# - NU1900 warnings are expected when the local NuGet vulnerability cache is not
#   writable. They mean the dependency audit could NOT run; they are not a pass.
#   See BUILD_STATUS.md. Pass OO_STRICT_AUDIT=1 to fail on them once the cache is
#   writable, so an unauditable restore can never be mistaken for an audited one.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 2

run_tests=1
[[ "${1:-}" == "--no-tests" ]] && run_tests=0

echo "== build =="
build_log="$(mktemp)"
if ! dotnet build OpenOutlook.sln --nologo -v q >"$build_log" 2>&1; then
    grep -E "error " "$build_log" | head -30
    rm -f "$build_log"
    echo "BUILD FAILED"
    exit 1
fi
errors=$(grep -cE "error " "$build_log" || true)
warnings=$(grep -c "warning NU1900" "$build_log" || true)
rm -f "$build_log"
echo "build ok (errors=$errors, NU1900 audit-skip warnings=$warnings)"

if [[ "${OO_STRICT_AUDIT:-0}" == "1" && "$warnings" != "0" ]]; then
    echo "NU1900 present and OO_STRICT_AUDIT=1: dependency vulnerability data was not retrieved."
    exit 1
fi

if [[ "$run_tests" == "1" ]]; then
    echo "== test =="
    if ! dotnet test OpenOutlook.sln --nologo --no-build; then
        echo "TESTS FAILED"
        exit 1
    fi
fi

echo "DEV CHECK PASSED"
