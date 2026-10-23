#!/usr/bin/env bash
# ci-lock.sh — machine-wide mutual exclusion for the BC/SQL container stack.
#
# WHY THIS EXISTS
# ---------------
# Every pipeline in this repo drives the stack with
#
#     docker compose down --remove-orphans
#     docker compose up -d
#
# and Compose derives the project name from the working directory's basename,
# which is `bc-linux` for every job everywhere. So two jobs that land on the
# same docker host address the SAME containers — `bc-linux-bc-1`,
# `bc-linux-sql-1` — no matter how their workspaces are laid out. The second
# job's `down` deletes the first job's BC mid-test.
#
# That is issue #24. What made it expensive to diagnose is that the symptom
# points nowhere near the cause: both jobs die with GitHub's generic
#
#     The runner has received a shutdown signal.
#     Process completed with exit code 143.
#
# which reads as "somebody stopped the runner service", not "another job
# deleted my container". Two PR builds on one self-hosted runner reproduced it
# exactly; retriggering either one alone passed with no other change.
#
# The fixed host ports in docker-compose.yml (7045-7089, 8080, 11433) mean the
# same thing from the other direction: even with distinct project names, two
# concurrent stacks on one machine cannot both bind them. Sharing the machine
# is inherently serial unless the caller has explicitly moved the ports, which
# is what the reusable workflows' `instance_slot` input does.
#
# So this is a lease, not an isolation scheme: hold it around the whole
# down/up/publish/test region and concurrent jobs queue instead of destroying
# each other. It is the same instinct as `download-artifacts.sh`'s flock on the
# artifact cache, extended to the resource the flock never covered.
#
# USAGE
#   scripts/ci-lock.sh acquire <name> [--wait-minutes N] [--max-hold-minutes N]
#   scripts/ci-lock.sh acquire-slot <base> --slots N [--wait-minutes N] [--max-hold-minutes N]
#   scripts/ci-lock.sh release <name>
#   scripts/ci-lock.sh status  <name>
#
# `acquire-slot` takes the first free stack of a pool of N: slot 0 is <base>,
# slot k is <base>-k, the same names a fixed instance_slot uses, so fixed and
# pooled jobs on one machine never share a stack. It writes the slot, the
# compose project name and the ports that go with them to $GITHUB_ENV.
# N is how many BC stacks the machine has the RAM for. Under snapshot mode
# (COMPOSE_FILE names docker-compose.snapshot.yml) bc uses host networking,
# so only slot 0 is used.
#
# `acquire` blocks until the lease is free, then prints — and, under GitHub
# Actions, appends to $GITHUB_ENV — the token that `release` needs:
#
#   BC_CI_LOCK_TOKEN=<random>
#
# so the release step in a later part of the same job identifies itself. A
# release without a matching token is refused rather than stealing somebody
# else's lease, which matters because the workflows run it with `if: always()`.
#
# WHY NOT flock(1)
# ----------------
# flock holds only as long as the process holding the fd. A CI job's steps are
# separate processes, so the lock has to outlive the step that took it. This
# keeps a lease FILE, refreshed by a background heartbeat, and treats a lease
# nobody has touched for BC_CI_LOCK_STALE_SECONDS as abandoned. That is also
# what makes the failure mode self-healing: a job killed the way the two in
# issue #24 were killed leaves its lease behind, its heartbeat dies with the
# job's process group, and the next job takes over ~2 minutes later instead of
# being blocked until somebody logs in.
#
# ENVIRONMENT
#   BC_CI_LOCK_DIR              where leases live. Default /var/tmp/bc-linux-ci-locks.
#                               MUST be shared by everything sharing the docker
#                               daemon — if your runners are themselves
#                               containers talking to a mounted docker socket,
#                               point this at a path mounted into all of them.
#   BC_CI_LOCK_TOKEN            set by acquire, consumed by release.
#   BC_CI_LOCK_STALE_SECONDS    abandonment threshold. Default 120.
#   BC_CI_LOCK_HEARTBEAT_SECONDS  refresh interval. Default 30.
#   BC_CI_LOCK_POLL_SECONDS     wait-loop interval. Default 5.
#   BC_CI_LOCK_DISABLE=1        make acquire/release no-ops. Escape hatch for a
#                               machine where the lease dir cannot be shared and
#                               the operator knows jobs never overlap.
#
# EXIT CODES
#   0  acquired / released / (status) free
#   1  usage error, or the lease could not be created at all
#   2  acquire timed out — another job still holds the stack
#   3  (status) held by somebody else

set -uo pipefail

LOCK_DIR="${BC_CI_LOCK_DIR:-/var/tmp/bc-linux-ci-locks}"
STALE_SECONDS="${BC_CI_LOCK_STALE_SECONDS:-120}"
HEARTBEAT_SECONDS="${BC_CI_LOCK_HEARTBEAT_SECONDS:-30}"
POLL_SECONDS="${BC_CI_LOCK_POLL_SECONDS:-5}"

usage() {
    sed -n '/^# USAGE/,/^#$/p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
}

# GNU coreutils first, BSD/macOS second. Nothing else is expected; a machine
# with neither gets an empty string, which the callers read as "no mtime", i.e.
# infinitely old — the conservative direction is to NOT steal, so both callers
# treat an unreadable mtime as fresh.
lease_mtime() {
    stat -c %Y "$1" 2>/dev/null || stat -f %m "$1" 2>/dev/null || echo ""
}

# The lease is a few `key=value` lines. Read one without sourcing the file —
# it is written by another job and must never be executed.
lease_field() {
    sed -n "s/^$2=//p" "$1" 2>/dev/null | head -1
}

describe_holder() {
    local lease="$1" age
    age=$(lease_age "$lease")
    printf '    held by : %s\n' "$(lease_field "$lease" who)"
    printf '    run     : %s\n' "$(lease_field "$lease" run)"
    printf '    since   : %s (%ss ago)\n' "$(lease_field "$lease" started)" "${age:-?}"
}

describe_holders() {
    local n
    for n in "$@"; do
        [ -f "$LOCK_DIR/$n.lease" ] || continue
        printf '  %s\n' "'$n'"
        describe_holder "$LOCK_DIR/$n.lease"
    done
}

lease_age() {
    local mtime now
    mtime=$(lease_mtime "$1")
    [ -n "$mtime" ] || return 0
    now=$(date +%s)
    echo $(( now - mtime ))
}

# Atomic create-or-fail. `set -o noclobber` turns `>` into O_CREAT|O_EXCL, so
# exactly one of N racing writers wins. (On NFS that guarantee is weaker; see
# BC_CI_LOCK_DIR above — a lease dir on NFS is not a supported configuration.)
try_create() {
    local lease="$1" payload="$2"
    ( set -o noclobber; printf '%s\n' "$payload" > "$lease" ) 2>/dev/null
}

start_heartbeat() {
    local lease="$1" token="$2" deadline="$3"
    # NOT setsid: this must die with the job's process group. A heartbeat that
    # outlives a killed job would keep refreshing a lease nobody owns, which is
    # precisely the wedge this script exists to avoid.
    #
    # The redirect is load-bearing under GitHub Actions: the runner waits for a
    # step's stdout/stderr pipes to close, so a background process still
    # holding them hangs the step forever.
    (
        while :; do
            sleep "$HEARTBEAT_SECONDS"
            [ -f "$lease" ] || break
            [ "$(lease_field "$lease" token)" = "$token" ] || break
            [ "$(date +%s)" -lt "$deadline" ] || break
            touch "$lease" 2>/dev/null || break
        done
    ) >/dev/null 2>&1 &
    echo $!
}

cmd_acquire() {
    local mode="$1" name="$2"; shift 2
    local wait_minutes=30 max_hold_minutes=120 slots=0
    while [ $# -gt 0 ]; do
        case "$1" in
            --wait-minutes)     wait_minutes="$2"; shift 2 ;;
            --max-hold-minutes) max_hold_minutes="$2"; shift 2 ;;
            --slots)            slots="$2"; shift 2 ;;
            *) echo "ci-lock: unknown option '$1'" >&2; usage ;;
        esac
    done

    if [ "$mode" = slot ]; then
        case "$slots" in
            ''|*[!0-9]*|0*) echo "ci-lock: --slots needs a whole number of at least 1, got '$slots'" >&2; usage ;;
        esac
        # Snapshot mode puts bc on host networking, where the slot's port
        # offsets do nothing: a second stack would collide on the ports.
        case "${COMPOSE_FILE:-}" in
            *docker-compose.snapshot.yml*)
                if [ "$slots" -gt 1 ]; then
                    echo "ci-lock: snapshot mode (host networking) — using slot 0 only, not a pool of $slots"
                    slots=1
                fi ;;
        esac
    fi

    # A plain acquire is a pool of one: just <name>.
    local -a names=("$name")
    if [ "$mode" = slot ]; then
        names=()
        local k
        for (( k = 0; k < slots; k++ )); do
            if [ "$k" -eq 0 ]; then names+=("$name"); else names+=("$name-$k"); fi
        done
    fi

    local lease token who run started
    token="$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')"
    who="${RUNNER_NAME:-$(hostname)}${GITHUB_JOB:+ / job $GITHUB_JOB}"
    run="${GITHUB_SERVER_URL:-}${GITHUB_REPOSITORY:+/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID}"
    started="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"

    local now start deadline hold_deadline payload
    start=$(date +%s)
    deadline=$(( start + wait_minutes * 60 ))
    hold_deadline=$(( start + max_hold_minutes * 60 ))
    payload="token=$token
who=$who
run=$run
started=$started
pid=$$"

    local announced=0 last_progress=$start slot
    while :; do
        for (( slot = 0; slot < ${#names[@]}; slot++ )); do
            name="${names[$slot]}"
            lease="$LOCK_DIR/$name.lease"
            if try_create "$lease" "$payload"; then
                # Verify after the fact. A waiter that judged this lease stale can
                # have deleted it in the window between our create and now, then
                # created its own — in which case we do NOT hold the stack and have
                # to go back to waiting. Cheap insurance against a double steal.
                sleep 1
                if [ "$(lease_field "$lease" token)" != "$token" ]; then
                    continue
                fi
                local hb
                hb=$(start_heartbeat "$lease" "$token" "$hold_deadline")
                printf 'hb=%s\n' "$hb" >> "$lease"
                local waited=$(( $(date +%s) - start ))
                if [ "$waited" -ge "$POLL_SECONDS" ]; then
                    echo "ci-lock: acquired '$name' after ${waited}s"
                else
                    echo "ci-lock: acquired '$name'"
                fi
                echo "BC_CI_LOCK_TOKEN=$token"
                [ -n "${GITHUB_ENV:-}" ] && echo "BC_CI_LOCK_TOKEN=$token" >> "$GITHUB_ENV"
                [ "$mode" = slot ] && write_slot_env "$slot" "$name"
                return 0
            fi

            # Somebody holds it. Decide whether they are still alive.
            local age
            age=$(lease_age "$lease")
            if [ -z "$age" ] && [ ! -f "$lease" ]; then
                # It vanished between the create attempt and the stat — retry
                # immediately rather than sleeping out a whole poll interval.
                (( slot-- ))
                continue
            fi

            if [ -n "$age" ] && [ "$age" -gt "$STALE_SECONDS" ]; then
                # Re-read after a pause and only steal if nothing moved. A holder
                # whose heartbeat is merely late gets a second chance; one whose
                # job was killed does not touch the file again, ever.
                local token_before
                token_before=$(lease_field "$lease" token)
                sleep "$POLL_SECONDS"
                if [ "$(lease_field "$lease" token)" = "$token_before" ]; then
                    age=$(lease_age "$lease")
                    if [ -n "$age" ] && [ "$age" -gt "$STALE_SECONDS" ]; then
                        echo "ci-lock: lease '$name' has not been refreshed for ${age}s — treating it as abandoned"
                        describe_holder "$lease"
                        rm -f "$lease"
                        (( slot-- ))
                    fi
                fi
                continue
            fi
        done

        now=$(date +%s)
        if [ "$now" -ge "$deadline" ]; then
            echo "ci-lock: ERROR — timed out after ${wait_minutes} min waiting for $(IFS=,; echo "${names[*]}")." >&2
            echo "         The BC/SQL container stack on this machine is in use by another job:" >&2
            describe_holders "${names[@]}" >&2
            echo "" >&2
            echo "         Jobs sharing a docker host share the compose project name and the" >&2
            echo "         host ports, so they cannot run at once (issue #24). To run them in" >&2
            echo "         parallel, let jobs take a free stack with the reusable workflow's" >&2
            echo "         'instance_slots' input (or give each job its own 'instance_slot')," >&2
            echo "         and make sure the machine has the RAM for one BC service tier and" >&2
            echo "         one SQL Server per stack." >&2
            return 2
        fi

        if [ "$announced" -eq 0 ]; then
            if [ "${#names[@]}" -gt 1 ]; then
                echo "ci-lock: all ${#names[@]} stacks ($(IFS=,; echo "${names[*]}")) are held — waiting up to ${wait_minutes} min."
                describe_holders "${names[@]}"
            else
                echo "ci-lock: '$name' is held — waiting up to ${wait_minutes} min."
                describe_holder "$lease"
            fi
            announced=1
        elif [ $(( now - last_progress )) -ge 60 ]; then
            echo "ci-lock:   still waiting for $(IFS=,; echo "${names[*]}") ($(( (now - start) / 60 )) min elapsed)"
            last_progress=$now
        fi
        sleep "$POLL_SECONDS"
    done
}

# The stack a slot stands for, for the steps after acquire-slot. Slot k moves
# every published port by k*100 (docker-compose.yml's defaults at slot 0).
write_slot_env() {
    local slot="$1" project="$2" off=$(( $1 * 100 ))
    echo "ci-lock: slot $slot → project $project, dev endpoint $(( 7049 + off ))"
    [ -n "${GITHUB_ENV:-}" ] || return 0
    {
        echo "COMPOSE_PROJECT_NAME=$project"
        echo "SQL_PORT=$(( 11433 + off ))"
        echo "BC_DEV_PORT=$(( 7049 + off ))"
        echo "BC_ODATA_PORT=$(( 7048 + off ))"
        echo "BC_API_PORT=$(( 7052 + off ))"
        echo "BC_MGMT_PORT=$(( 7045 + off ))"
        echo "BC_CLIENT_PORT=$(( 7085 + off ))"
        echo "BC_WEBCLIENT_PORT=$(( 8080 + off ))"
    } >> "$GITHUB_ENV"
}

cmd_release() {
    local name="$1"
    local lease="$LOCK_DIR/$name.lease"
    local token="${BC_CI_LOCK_TOKEN:-}"

    if [ ! -f "$lease" ]; then
        echo "ci-lock: '$name' was already free"
        return 0
    fi
    if [ -z "$token" ]; then
        # Reached when acquire never ran (an early step failed) or when
        # $GITHUB_ENV did not propagate. Releasing blind here would hand the
        # stack to a second job while this one is still using it, so don't.
        echo "ci-lock: BC_CI_LOCK_TOKEN is unset — leaving '$name' alone."
        echo "         It will be reclaimed automatically after ${STALE_SECONDS}s without a heartbeat."
        return 0
    fi
    if [ "$(lease_field "$lease" token)" != "$token" ]; then
        echo "ci-lock: '$name' is held by a different job now — not releasing."
        describe_holder "$lease"
        return 0
    fi

    local hb
    hb=$(lease_field "$lease" hb)
    rm -f "$lease"
    # After the lease is gone the heartbeat exits on its own within one
    # interval; killing it just makes that immediate.
    [ -n "$hb" ] && kill "$hb" 2>/dev/null
    echo "ci-lock: released '$name'"
    return 0
}

cmd_status() {
    local name="$1"
    local lease="$LOCK_DIR/$name.lease"
    if [ ! -f "$lease" ]; then
        echo "ci-lock: '$name' is free"
        return 0
    fi
    echo "ci-lock: '$name' is held"
    describe_holder "$lease"
    return 3
}

main() {
    [ $# -ge 2 ] || usage
    local cmd="$1" name="$2"; shift 2

    # Keep the lease filename to something a filesystem is guaranteed to
    # accept: the caller builds the name from a compose project name, which
    # may carry anything a directory basename can.
    name="$(printf '%s' "$name" | tr -c 'A-Za-z0-9._-' '_')"

    if [ "${BC_CI_LOCK_DISABLE:-}" = "1" ]; then
        echo "ci-lock: disabled via BC_CI_LOCK_DISABLE — '$cmd $name' is a no-op"
        return 0
    fi

    if ! mkdir -p "$LOCK_DIR" 2>/dev/null; then
        echo "ci-lock: ERROR — cannot create lease directory $LOCK_DIR" >&2
        echo "         Set BC_CI_LOCK_DIR to a writable path shared by every job on this machine." >&2
        return 1
    fi
    # Shared by design: two runner services usually run as different users, and
    # both have to be able to take the lease. Best-effort — a pre-existing
    # directory owned by somebody else will not change mode, and does not need
    # to as long as it is already group/world writable.
    chmod 1777 "$LOCK_DIR" 2>/dev/null || true

    case "$cmd" in
        acquire) cmd_acquire plain "$name" "$@" ;;
        acquire-slot) cmd_acquire slot "$name" "$@" ;;
        release) cmd_release "$name" ;;
        status)  cmd_status  "$name" ;;
        *) echo "ci-lock: unknown command '$cmd'" >&2; usage ;;
    esac
}

main "$@"
