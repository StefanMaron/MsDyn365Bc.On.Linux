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

# A red run must fail fast, not hang: exit 124 means ci-lock blocked.
lock() { timeout 20 bash "$here/ci-lock.sh" "$@"; }

# acquire-slot <n> <out-file>: run like a job step, with its own $GITHUB_ENV
take() {
  GITHUB_ENV="$2.env" lock acquire-slot bc-linux --slots "$1" --wait-minutes 1 > "$2" 2>&1
}
token_of() { sed -n 's/^BC_CI_LOCK_TOKEN=//p' "$1.env" 2>/dev/null; }
project_of() { sed -n 's/^COMPOSE_PROJECT_NAME=//p' "$1.env" 2>/dev/null; }
slot_of() {
  local p; p=$(project_of "$1")
  case "$p" in bc-linux) echo 0 ;; bc-linux-*) echo "${p#bc-linux-}" ;; esac
}
# wait_for <file> <pattern>: poll up to 20s, instead of guessing a sleep
wait_for() {
  local i
  for (( i = 0; i < 40; i++ )); do grep -q "$2" "$1" 2>/dev/null && return 0; sleep 0.5; done
  return 1
}

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
wait_for "$work/job4" 'are held' && kill -0 $waiter 2>/dev/null \
  && ok "a fourth job waits while all slots are taken" || bad "a fourth job did not wait"
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

# nothing reads a slot number, so only the project name and ports are written
grep -q '^BC_CI_SLOT=' "$work/job5.env" && bad "BC_CI_SLOT is written but unused" || ok "no unused BC_CI_SLOT"

# a timeout names every held lease, not just the last one tried
release "$work/job5"; rm -f "$work/job5.env"
GITHUB_ENV="$work/fixed1.env" lock acquire bc-linux-1 > /dev/null 2>&1
GITHUB_ENV="$work/held.env" RUNNER_NAME=holder-zero lock acquire bc-linux > /dev/null 2>&1
GITHUB_ENV="$work/held1.env" lock acquire-slot bc-linux --slots 2 --wait-minutes 0 > "$work/timeout" 2>&1
rc=$?
[ $rc = 2 ] && grep -q "'bc-linux'" "$work/timeout" && grep -q "'bc-linux-1'" "$work/timeout" \
  && grep -q 'holder-zero' "$work/timeout" && ok "a timeout lists every held slot" \
  || { bad "the timeout (rc $rc) does not list every held slot"; cat "$work/timeout"; }
BC_CI_LOCK_TOKEN=$(sed -n 's/^BC_CI_LOCK_TOKEN=//p' "$work/held.env") lock release bc-linux > /dev/null
BC_CI_LOCK_TOKEN=$(sed -n 's/^BC_CI_LOCK_TOKEN=//p' "$work/fixed1.env") lock release bc-linux-1 > /dev/null

# --slots must be a positive whole number; anything else is a usage error
for v in abc -1 0 1.5 ''; do
  out=$(GITHUB_ENV="$work/bad.env" lock acquire-slot bc-linux --slots "$v" --wait-minutes 0 2>&1); rc=$?
  { [ $rc = 1 ] && grep -q -- '--slots' <<< "$out" && ! grep -q 'integer expression' <<< "$out"; } \
    && ok "--slots '$v' is refused" || bad "--slots '$v' gave rc $rc: $out"
  [ -f "$work/bad.env" ] && bad "--slots '$v' still took a lease" && rm -f "$work/bad.env"
done

# snapshot mode runs bc on host networking, so the port offsets do nothing:
# the pool shrinks to slot 0 and jobs queue as they would without it
GITHUB_ENV="$work/held.env" lock acquire bc-linux > /dev/null 2>&1
COMPOSE_FILE=docker-compose.yml:docker-compose.snapshot.yml \
  GITHUB_ENV="$work/snap.env" lock acquire-slot bc-linux --slots 3 --wait-minutes 0 > "$work/snap" 2>&1
rc=$?
[ $rc = 2 ] && grep -q 'snapshot' "$work/snap" && ok "snapshot mode uses slot 0 only" \
  || { bad "snapshot mode took a pooled slot (rc $rc)"; cat "$work/snap"; }
BC_CI_LOCK_TOKEN=$(sed -n 's/^BC_CI_LOCK_TOKEN=//p' "$work/held.env") lock release bc-linux > /dev/null
[ -f "$work/snap.env" ] && release "$work/snap"

exit $fail
