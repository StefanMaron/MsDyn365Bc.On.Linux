#!/usr/bin/env bash
# Tests for `ci-lock.sh acquire-slot`: jobs take the first free stack from a
# pool. No docker, no GitHub. Run: bash scripts/test-ci-lock-slots.sh
set -u
here="$(cd "$(dirname "$0")" && pwd)"
work=$(mktemp -d)
export BC_CI_LOCK_DIR="$work/locks" BC_CI_LOCK_POLL_SECONDS=1 BC_CI_LOCK_HEARTBEAT_SECONDS=1
fail=0
ok()  { echo "ok   $1"; }
bad() { echo "FAIL $1"; fail=1; }

lock() { bash "$here/ci-lock.sh" "$@"; }

# acquire-slot <n> <out-file>: run like a job step, with its own $GITHUB_ENV
take() {
  GITHUB_ENV="$2.env" lock acquire-slot bc-linux --slots "$1" --wait-minutes 1 > "$2" 2>&1
}
slot_of()  { sed -n 's/^BC_CI_SLOT=//p' "$1.env" 2>/dev/null; }
token_of() { sed -n 's/^BC_CI_LOCK_TOKEN=//p' "$1.env" 2>/dev/null; }
project_of() { sed -n 's/^COMPOSE_PROJECT_NAME=//p' "$1.env" 2>/dev/null; }

release() {
  BC_CI_LOCK_TOKEN=$(token_of "$1") lock release "$(project_of "$1")" > /dev/null
}

cleanup() {
  for f in "$work"/job*; do [ -f "$f.env" ] && release "$f"; done
  rm -rf "$work"
}
trap cleanup EXIT

# three jobs at once, three slots: each gets its own
for i in 1 2 3; do take 3 "$work/job$i" & done
wait
slots=$(for i in 1 2 3; do slot_of "$work/job$i"; done | sort | tr '\n' ' ')
[ "$slots" = "0 1 2 " ] && ok "three jobs take slots 0, 1 and 2" || bad "three jobs got slots '$slots'"

# the job learns its stack: project name and ports follow the slot
for i in 1 2 3; do
  s=$(slot_of "$work/job$i"); p=$(project_of "$work/job$i")
  want=bc-linux; [ "$s" != 0 ] && want="bc-linux-$s"
  [ "$p" = "$want" ] || bad "slot $s got project '$p', want '$want'"
  grep -q "^BC_DEV_PORT=$(( 7049 + s * 100 ))$" "$work/job$i.env" \
    || bad "slot $s has no BC_DEV_PORT=$(( 7049 + s * 100 ))"
  grep -q "^SQL_PORT=$(( 11433 + s * 100 ))$" "$work/job$i.env" \
    || bad "slot $s has no SQL_PORT=$(( 11433 + s * 100 ))"
done
[ $fail = 0 ] && ok "project name and ports follow the slot"

# a fourth job waits, and takes the slot that is released first
take 3 "$work/job4" & waiter=$!
sleep 3
kill -0 $waiter 2>/dev/null && ok "a fourth job waits while all slots are taken" \
  || bad "a fourth job did not wait"
freed=$(slot_of "$work/job2")
release "$work/job2"; rm -f "$work/job2.env"
wait $waiter
[ -n "$freed" ] && [ "$(slot_of "$work/job4")" = "$freed" ] && ok "it takes the freed slot $freed" \
  || bad "it took slot '$(slot_of "$work/job4")', freed was $freed"

# a job with a fixed instance_slot holds the same lease, so the pool skips it
for f in "$work"/job*; do [ -f "$f.env" ] && release "$f" && rm -f "$f.env"; done
GITHUB_ENV="$work/fixed.env" lock acquire bc-linux > /dev/null 2>&1
take 2 "$work/job5"
[ "$(slot_of "$work/job5")" = 1 ] && ok "a fixed slot 0 job makes the pool use slot 1" \
  || bad "the pool took slot '$(slot_of "$work/job5")' next to a fixed slot 0 job"
BC_CI_LOCK_TOKEN=$(sed -n 's/^BC_CI_LOCK_TOKEN=//p' "$work/fixed.env") lock release bc-linux > /dev/null

exit $fail
