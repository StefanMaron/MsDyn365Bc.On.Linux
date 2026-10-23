#!/usr/bin/env bash
# Unit tests for scripts/app-artifact-name.sh. Run: bash scripts/test-app-artifact-name.sh
set -u
. "$(dirname "$0")/app-artifact-name.sh"

fail=0
# check <want> <dir> [all dirs of the run...]
check() {
  local want="$1"; shift
  local got; got=$(app_artifact_name "$@")
  if [ "$got" = "$want" ]; then echo "ok   $1 -> $got"; else echo "FAIL $1 -> $got (want $want)"; fail=1; fi
}

# no collision: the plain folder name, as before the helper existed
check "app" "app" "app" "test"
check "test" "test" "app" "test"
check "MyApp" "src/MyApp" "src/MyApp" "src/MyTest"
check "MyApp" "./src/MyApp/" "./src/MyApp/" "src/MyTest"
check "MyApp" "src/MyApp"

# collision: the path-based name, for the colliding dirs only
all=("apps/Sales/app" "apps/Shipping/app" "apps/Sales/test" "apps/Core")
check "apps-Sales-app" "apps/Sales/app" "${all[@]}"
check "apps-Shipping-app" "apps/Shipping/app" "${all[@]}"
check "test" "apps/Sales/test" "${all[@]}"
check "Core" "apps/Core" "${all[@]}"
check "my app-test app" "my app/test app" "my app/test app" "other/test app"

# a dir literally named project/ is the consumer's own, not the checkout dir
check "project-app" "project/app" "project/app" "app"
check "app" "app" "project/app" "app"

# listing the same dir twice is not a collision
check "app" "src/app" "src/app" "./src/app/"

exit $fail
