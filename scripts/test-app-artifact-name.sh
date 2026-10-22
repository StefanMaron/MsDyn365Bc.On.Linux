#!/usr/bin/env bash
# Unit tests for scripts/app-artifact-name.sh. Run: bash scripts/test-app-artifact-name.sh
set -u
. "$(dirname "$0")/app-artifact-name.sh"

fail=0
check() {
  local got; got=$(app_artifact_name "$1")
  if [ "$got" = "$2" ]; then echo "ok   $1 -> $got"; else echo "FAIL $1 -> $got (want $2)"; fail=1; fi
}

check "app" "app"
check "test" "test"
check "apps/Sales/app" "apps-Sales-app"
check "apps/Sales/test" "apps-Sales-test"
check "project/apps/Sales/app" "apps-Sales-app"
check "./apps/Sales/app/" "apps-Sales-app"
check "my app/test app" "my app-test app"

# the reason the helper exists: one folder name, two apps, two different names
[ "$(app_artifact_name apps/A/app)" != "$(app_artifact_name apps/B/app)" ] \
  && echo "ok   distinct names for apps/A/app and apps/B/app" \
  || { echo "FAIL apps/A/app and apps/B/app collide"; fail=1; }

exit $fail
