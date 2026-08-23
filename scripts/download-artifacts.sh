#!/bin/bash
# Download BC artifacts (platform + country) to a target directory.
# Supports both public and insider artifact URLs.
#
# Performance design:
#   - App and platform zips are downloaded IN PARALLEL to a fast temp dir
#     (host tmpfs / runner /tmp) rather than directly to the destination
#     volume.  This avoids writing the raw zip into the (slower) Docker
#     named volume and cuts the effective I/O to the volume by ~50%.
#   - Each zip is fetched with MULTIPLE PARALLEL BYTE-RANGE STREAMS
#     (BC_DL_STREAMS per file, default 8). Azure Front Door serves a
#     POP-cache MISS at only ~4-8 MB/s PER CONNECTION (origin fetch),
#     while the same POP serves cache hits at 200+ MB/s — that's the
#     source of the notorious 30s-vs-6min variance. AFD uses chunked
#     object caching, so N concurrent range requests pull origin chunks
#     in parallel and multiply cold-miss throughput by ~N. The direct
#     blob endpoint (bcartifacts.blob.core.windows.net) is no longer an
#     option: Microsoft put it behind a network security perimeter
#     (403), so AFD is the only door.
#   - Every range stream aborts and retries when it crawls below
#     100 KB/s for 60s (--speed-limit/--speed-time) — a reconnect usually
#     lands a healthier origin connection, and by then AFD has cached
#     the chunks already pulled. The threshold must stay low enough that
#     many parallel streams sharing a narrow home pipe don't trip it.
#   - Extraction is MULTI-THREADED (python zipfile + thread pool; plain
#     unzip is single-threaded and took as long as the download), and the
#     app zip is extracted WHILE the platform zip is still downloading.
#   - Timing is logged for each phase so you can see exactly where time
#     goes: version resolution, download, and extraction.
#
# Caching:
#   A successful run leaves a `.bc-artifact-cache` stamp in <dest> recording
#   the exact app + platform URLs it downloaded. A later run whose resolved
#   URLs match that stamp — and whose extraction still looks intact — returns
#   in milliseconds instead of re-fetching ~2 GB.
#
#   The stamp is keyed on the RESOLVED FULL version, not the requested one,
#   so a short version ("28.3") that Microsoft has since hotfixed resolves to
#   a new URL, misses the cache, and re-downloads. That property is the whole
#   reason the resolve step still runs on a cache hit: bc-linux deliberately
#   tracks the newest build of a short version (see CLAUDE.md, "The version
#   matrix is discovered at run time").
#
#   This is NOT actions/cache — nothing is uploaded or downloaded from
#   GitHub, and CLAUDE.md's ban on caching artifacts THROUGH GitHub's cache
#   still stands, for the reasons recorded there. This is opportunistic reuse
#   of whatever the filesystem already holds: a no-op on an ephemeral hosted
#   runner (empty dir every job, one extra index fetch of ~200 ms), and the
#   difference between a ~30-60s fetch phase and ~0 on a self-hosted runner
#   or a local dev box.
#
#   BC_ARTIFACT_REFRESH=1 forces a miss. When the index is unreachable and a
#   stamp exists for the same REQUEST (type/version/country, or URL), the
#   cache is used with a warning rather than failing the run — an offline box
#   with warm artifacts now boots instead of erroring out.
#
# Usage:
#   With full URL:  download-artifacts.sh <url> <dest>
#   With parts:     download-artifacts.sh <type> <version> <country> <dest>
set -e

_ms() { date +%s%3N; }

# ── Serialize concurrent runs against the same dest ────────────────────────
# Two invocations sharing one cache dir (several self-hosted runners on one
# host, or a matrix pointed at a shared BC_ARTIFACTS_DIR) would otherwise
# race: both miss, both clear, both extract into the same tree. The lock makes
# the second one wait and then hit the cache the first one just wrote.
#
# Taken before argument parsing so the version resolve happens once, inside
# the lock, rather than once per waiter. flock is util-linux — present on
# GitHub runners and in the bc-runner image — but a box without it runs
# unlocked (previous behavior) instead of failing.
if [ -z "${BC_ARTIFACT_LOCKED:-}" ] && command -v flock >/dev/null 2>&1; then
    case $# in
        2) _LOCK_DEST="$2" ;;
        4) _LOCK_DEST="$4" ;;
        *) _LOCK_DEST="" ;;
    esac
    if [ -n "$_LOCK_DEST" ] && mkdir -p "$_LOCK_DEST" 2>/dev/null; then
        export BC_ARTIFACT_LOCKED=1
        exec flock "$_LOCK_DEST/.bc-artifact-lock" "$0" "$@"
    fi
fi

# Parallel range streams per file (two files download concurrently, so the
# total connection count is 2x this). 16 gives ~16x cold-miss throughput;
# override with BC_DL_STREAMS.
STREAMS="${BC_DL_STREAMS:-16}"

# Share of the total stream budget handed to the LARGER of the two zips,
# as a percentage. 50 = even split, which is the default and what you
# want in almost every case.
#
# This started at 70 on the theory that biasing streams would land the
# bigger zip early and let its extraction overlap the other download.
# Measured across two environments, it doesn't:
#
#   Pageworks re-run, 122 MB/s: platform (22 streams) finished at
#   17943ms, app (10 streams) at 17945ms. Two milliseconds apart.
#
# Bandwidth doesn't reallocate proportionally to stream count the way
# that model assumed, so no overlap window opens and the bias buys
# nothing. Worse, it made the app zip the extraction tail, and the app
# zip is dominated by one ~900 MB BusinessCentral-*.bak — a single zip
# entry, so single-threaded no matter how many workers the pool has,
# where the platform zip's 6600 files parallelize fine.
#
# The knob stays for genuinely slow links where the two downloads DO
# separate in time; the default no longer guesses.
BIG_SHARE="${BC_DL_BIG_SHARE:-50}"

# Fetch one byte range to a part file, with slow-transfer abort + retries.
# A stream stuck below 100 KB/s for 60s is killed and reconnected — AFD
# origin connections occasionally degenerate, and a fresh connection
# (plus the chunks AFD cached meanwhile) is almost always faster. The
# threshold is deliberately low: on a narrow pipe (e.g. 100 Mbit home
# connection) 32 parallel streams legitimately run at ~300 KB/s each, and
# an aggressive watchdog would kill healthy streams. The final attempt
# runs with no watchdog at all — better slow than failed.
_fetch_range() {
    local url="$1" out="$2" start="$3" end="$4"
    local want=$((end - start + 1)) attempt code got
    for attempt in 1 2 3 4; do
        local speed_args=(--speed-limit 102400 --speed-time 60)
        if [ "$attempt" -eq 4 ]; then speed_args=(); fi
        code=$(curl -s --http1.1 --retry 2 --retry-all-errors \
                    "${speed_args[@]}" \
                    -r "$start-$end" "$url" -o "$out" \
                    -w '%{http_code}' 2>/dev/null) || code="exit$?"
        got=$(stat -c%s "$out" 2>/dev/null || echo 0)
        if [ "$code" = "206" ] && [ "$got" -eq "$want" ]; then
            return 0
        fi
        echo "[artifacts] WARN: range $start-$end attempt $attempt: http=$code got=$got of $want bytes" >&2
        sleep $((attempt * 2))
    done
    echo "[artifacts] ERROR: range $start-$end failed after 4 attempts" >&2
    return 1
}

# Content-Length of a URL, or empty when the server won't say. Used to
# plan the stream split before either download starts.
_head_size() {
    curl -sfI --http1.1 --retry 3 --retry-all-errors "$1" | tr -d '\r' \
        | awk 'tolower($1)=="content-length:"{print $2}' | tail -1
}

# Download a URL using N parallel byte-range streams (default $STREAMS),
# then stitch the parts together. Falls back to a plain single-stream curl
# when the server doesn't advertise range support or a usable
# Content-Length.
_ranged_download() {
    local url="$1" out="$2" streams="${3:-$STREAMS}"
    local head size
    head=$(curl -sfI --http1.1 --retry 3 --retry-all-errors "$url" | tr -d '\r') || head=""
    size=$(echo "$head" | awk 'tolower($1)=="content-length:"{print $2}' | tail -1)
    if ! echo "$head" | grep -qi '^accept-ranges: *bytes' || \
       ! echo "$size" | grep -qE '^[0-9]+$' || [ "$size" -lt $((16 * 1024 * 1024)) ]; then
        curl -sSL --retry 3 --retry-all-errors --http1.1 \
             --speed-limit 102400 --speed-time 60 "$url" -o "$out"
        return
    fi

    local chunk=$(( (size + streams - 1) / streams ))
    local i start end pids=() rc=0
    for ((i = 0; i < streams; i++)); do
        start=$((i * chunk))
        end=$((start + chunk - 1))
        if [ "$end" -ge "$size" ]; then end=$((size - 1)); fi
        if [ "$start" -gt "$end" ]; then break; fi
        _fetch_range "$url" "$out.part$i" "$start" "$end" &
        pids+=($!)
    done
    for pid in "${pids[@]}"; do
        if ! wait "$pid"; then rc=1; fi
    done
    if [ "$rc" -ne 0 ]; then return 1; fi

    # Explicit index loop — a glob would order part10 before part2.
    : > "$out"
    for ((i = 0; i < ${#pids[@]}; i++)); do
        cat "$out.part$i" >> "$out"
        rm -f "$out.part$i"
    done
    local total
    total=$(stat -c%s "$out")
    if [ "$total" -ne "$size" ]; then
        echo "[artifacts] ERROR: stitched $total bytes, expected $size ($url)" >&2
        return 1
    fi
}

# Multi-threaded zip extraction (plain `unzip` is single-threaded and was
# taking as long as the 16-stream download it follows). zlib decompression
# and file writes release the GIL, so a Python thread pool scales across
# cores. Extracts EVERYTHING deliberately: the old selective
# `unzip 'ServiceTier/*' 'applications/*' ... || unzip <all>` never
# actually selected — the zip's dir is `Applications/` (capital A), the
# unmatched lowercase pattern made unzip exit 11, and the fallback
# extracted the full zip on every run. Selection would save ~10 MB of
# ~2 GB today and risks silently dropping dirs consumers need
# (Applications/ holds the test-framework .apps that stage-symbols.py
# and the entrypoint's install-for-tenant loop walk). BC artifact zips
# are built on Windows (no unix modes/symlinks), so zipfile loses
# nothing vs unzip.
_extract_zip() {
    local zip="$1" dest="$2"
    python3 - "$zip" "$dest" <<'PYEOF'
import os, sys, zipfile
from concurrent.futures import ThreadPoolExecutor

os.path.altsep = '\\'

zip_path, dest = sys.argv[1], sys.argv[2]

with zipfile.ZipFile(zip_path) as zf:
    infos = zf.infolist()
files = [i for i in infos if not i.is_dir()]

# Pre-create every directory up front: zipfile's internal makedirs is not
# race-safe, and the workers below extract concurrently.
for i in files:
    safe_filename = i.filename.replace('\\', '/').lstrip('/')
    
    d = os.path.dirname(safe_filename)
    if d:
        os.makedirs(os.path.join(dest, d), exist_ok=True)

# Largest-first round-robin keeps the per-worker byte counts balanced.
files.sort(key=lambda i: i.file_size, reverse=True)
workers = min(8, os.cpu_count() or 4)
chunks = [files[i::workers] for i in range(workers)]

def extract(chunk):
    # One ZipFile handle per worker — a shared handle serializes reads.
    with zipfile.ZipFile(zip_path) as zf:
        for info in chunk:
            zf.extract(info, dest)

with ThreadPoolExecutor(workers) as ex:
    for _ in ex.map(extract, chunks):
        pass
PYEOF
}

# ── Cache stamp helpers ────────────────────────────────────────────────────
# The stamp records two keys:
#   key=     the resolved app+platform URLs. A match means "the bytes on disk
#            are exactly what this invocation would download."
#   request= the arguments as GIVEN. Only consulted when version resolution
#            fails (no network / index down), as a last-resort "this dir was
#            built from the same request, use it rather than dying."
STAMP_NAME=".bc-artifact-cache"

# An extraction is only usable if BOTH halves landed. These are the same two
# paths the entrypoint waits for when BC_ARTIFACT_URL=skip, so agreeing with
# it here means a cache hit can never satisfy this script but starve the
# container.
_cache_intact() {
    [ -f "$1/app/manifest.json" ] && [ -d "$1/platform/ServiceTier" ]
}

_stamp_field() {
    # _stamp_field <dest> <field>
    [ -f "$1/$STAMP_NAME" ] || return 1
    sed -n "s|^$2=||p" "$1/$STAMP_NAME" | head -1
}

# Everything this script created, and nothing else. Deliberately NOT
# `rm -rf "$DEST"` — DEST is a bind mount / named volume mount point in every
# caller, and removing it would break the mount rather than clear it.
_cache_clear() {
    # ${1:?} so an empty dest can never turn this into `rm -rf /app /platform`.
    rm -rf "${1:?}/app" "${1:?}/platform" "${1:?}/$STAMP_NAME"
}

# Parse arguments: either (url, dest) or (type, version, country, dest)
if [ $# -eq 2 ]; then
    APP_URL="$1"
    DEST="$2"
    # Derive platform URL: replace country segment with "platform"
    PLATFORM_URL=$(echo "$APP_URL" | sed 's|/[^/]*$|/platform|')
    REQUEST_KEY="url|$APP_URL"
elif [ $# -eq 4 ]; then
    BC_TYPE="$1"; BC_VERSION="$2"; BC_COUNTRY="${3,,}"; DEST="$4"
    REQUEST_KEY="args|$1|$2|${3,,}"
    BASE_URL="https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net"

    # Resolve short version (e.g. "27.5") to full version (e.g. "27.5.46862.48612)
    # using the per-country JSON index file that Microsoft maintains for
    # navcontainerhelper:
    #
    #   https://bcartifacts.blob.core.windows.net/<type>/indexes/<country>.json
    #
    # This is the canonical approach used by BcContainerHelper's
    # QueryArtifactsFromIndex (HelperFunctions.ps1:1721) — it's a static
    # JSON object, NOT the list-blobs API. Avoids the AFD list-blobs cache
    # poisoning that plagued earlier versions of this script
    # (microsoft/navcontainerhelper#4119), which would intermittently return
    # stale 27.0/27.1/27.2 entries when asked for prefix=27.5.
    #
    # To skip the resolver entirely, pass a fully-qualified version like
    # "27.5.46862.48612" via BC_VERSION — the regex below sees three parts
    # and goes straight to the download.
    if ! echo "$BC_VERSION" | grep -qP '^\d+\.\d+\.\d+'; then
        echo "[artifacts] Resolving version $BC_VERSION via Microsoft's index file..."
        T_RESOLVE=$(_ms)
        REQUESTED_PREFIX="$BC_VERSION"
        # Released versions live on bcartifacts; versions Microsoft hasn't
        # shipped yet (BC 29 as of 2026-08) live only on the insider storage
        # account, which serves the same index layout and — unlike the old
        # bcinsider blob endpoint — needs no SAS token. Try released first and
        # fall back, so this stays a no-op for every version that IS released.
        INDEX_BASES="$BASE_URL https://bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net"
        INDEX_URL="$BASE_URL/${BC_TYPE}/indexes/${BC_COUNTRY}.json"
        RESOLVED=""
        # Three attempts in case of transient network errors. The index
        # file is a regular cached blob, so it doesn't suffer the
        # list-blobs API's stale-cache problem; one retry is usually
        # plenty.
        for attempt in 1 2 3; do
          for INDEX_BASE in $INDEX_BASES; do
            INDEX_URL="$INDEX_BASE/${BC_TYPE}/indexes/${BC_COUNTRY}.json"
            RESOLVED=$(curl -sf --retry 2 --retry-delay 2 "$INDEX_URL" 2>/dev/null | \
                BC_PREFIX="$REQUESTED_PREFIX" python3 -c "
import json, os, sys
prefix = os.environ['BC_PREFIX'] + '.'
try:
    data = json.load(sys.stdin)
except Exception as e:
    sys.exit(1)
versions = [d['Version'] for d in data if d.get('Version', '').startswith(prefix)]
if not versions:
    sys.exit(0)
def vkey(v):
    return tuple(int(x) for x in v.split('.'))
versions.sort(key=vkey)
print(versions[-1])
" 2>/dev/null || true)
            if [ -n "$RESOLVED" ] && echo "$RESOLVED" | grep -q "^${REQUESTED_PREFIX}\."; then
                RESOLVED_BASE="$INDEX_BASE"
                break
            fi
            RESOLVED=""
          done
          [ -n "$RESOLVED" ] && break
          echo "[artifacts] WARN: attempt $attempt — index file unreachable or no '$REQUESTED_PREFIX.x' versions found; retrying..."
          sleep 3
        done
        if [ -z "$RESOLVED" ]; then
            # Before failing: if this dest already holds a complete extraction
            # built from the SAME request, the index being unreachable is not a
            # reason to take the box down. We can't tell whether a hotfix has
            # shipped since, so say so loudly and carry on with what we have.
            if [ "${BC_ARTIFACT_REFRESH:-}" != "1" ] && _cache_intact "$DEST" \
               && [ "$(_stamp_field "$DEST" request || true)" = "$REQUEST_KEY" ]; then
                echo "[artifacts] WARN: version index unreachable — falling back to the cached"
                echo "[artifacts] WARN: artifacts already in $DEST ($(_stamp_field "$DEST" key || echo unknown))."
                echo "[artifacts] WARN: These may be behind a newer $REQUESTED_PREFIX.x hotfix."
                exit 0
            fi
            echo "[artifacts] ERROR: Could not resolve version $REQUESTED_PREFIX from $INDEX_URL"
            echo "[artifacts] Workaround: pin BC_VERSION to a fully-qualified version, e.g.:"
            echo "[artifacts]   BC_VERSION=27.5.46862.48612 docker compose up -d --wait"
            exit 1
        fi
        echo "[artifacts] Resolved: $REQUESTED_PREFIX → $RESOLVED via $RESOLVED_BASE ($(( $(_ms) - T_RESOLVE ))ms)"
        BC_VERSION="$RESOLVED"
        # Download from whichever account actually had the version.
        BASE_URL="$RESOLVED_BASE"
    fi

    APP_URL="$BASE_URL/$BC_TYPE/$BC_VERSION/$BC_COUNTRY"
    PLATFORM_URL="$BASE_URL/$BC_TYPE/$BC_VERSION/platform"
else
    echo "Usage: $0 <artifact-url> <dest>"
    echo "   or: $0 <type> <version> <country> <dest>"
    exit 1
fi

echo "[artifacts] App URL:      $APP_URL"
echo "[artifacts] Platform URL: $PLATFORM_URL"

CACHE_KEY="v1|$APP_URL|$PLATFORM_URL"

# ── Cache check ────────────────────────────────────────────────────────────
if [ "${BC_ARTIFACT_REFRESH:-}" = "1" ]; then
    echo "[artifacts] BC_ARTIFACT_REFRESH=1 — ignoring any cached artifacts"
    _cache_clear "$DEST"
elif [ "$(_stamp_field "$DEST" key || true)" = "$CACHE_KEY" ] && _cache_intact "$DEST"; then
    echo "[artifacts] CACHE HIT — $DEST already holds these exact artifacts ($(du -sh "$DEST" 2>/dev/null | cut -f1)). Skipping download."
    exit 0
elif [ -e "$DEST/app" ] || [ -e "$DEST/platform" ]; then
    # Stale (different version), or a torn extraction from an interrupted run.
    # Either way the safe move is to start clean: a half-extracted service tier
    # fails much later and much more confusingly than a re-download.
    echo "[artifacts] Cache miss — clearing stale/incomplete artifacts in $DEST"
    echo "[artifacts]   cached:  $(_stamp_field "$DEST" key || echo '(no stamp)')"
    echo "[artifacts]   wanted:  $CACHE_KEY"
    _cache_clear "$DEST"
fi

# Download zips to a temp dir (host /tmp is fast tmpfs/SSD, not a Docker volume).
# This avoids writing ~1-3 GB of zip data into the destination volume just to
# immediately delete them after extraction — halving the volume write load.
TMPDIR_DL=$(mktemp -d)
trap 'rm -rf "$TMPDIR_DL"' EXIT

mkdir -p "$DEST/app" "$DEST/platform"

# ── Plan the stream split ──────────────────────────────────────────────────
# Only does anything when BC_DL_BIG_SHARE is moved off its default of 50.
# The two HEAD requests (~20ms) are skipped entirely at 50, since an even
# split needs no size information. When a size lookup fails we fall back
# to an even split; _ranged_download re-checks the headers itself and
# degrades to a plain single-stream curl when the server won't do ranges,
# so a missing size here is never fatal.
TOTAL_STREAMS=$(( STREAMS * 2 ))
APP_STREAMS=$STREAMS
PLAT_STREAMS=$STREAMS
if [ "$BIG_SHARE" != "50" ]; then
    APP_SIZE=$(_head_size "$APP_URL")
    PLAT_SIZE=$(_head_size "$PLATFORM_URL")
    if echo "$APP_SIZE" | grep -qE '^[0-9]+$' && echo "$PLAT_SIZE" | grep -qE '^[0-9]+$'; then
        BIG_STREAMS=$(( TOTAL_STREAMS * BIG_SHARE / 100 ))
        # Never starve the smaller file: it still has to finish, and one
        # stream would make it the new tail.
        [ "$BIG_STREAMS" -gt $(( TOTAL_STREAMS - 2 )) ] && BIG_STREAMS=$(( TOTAL_STREAMS - 2 ))
        [ "$BIG_STREAMS" -lt 2 ] && BIG_STREAMS=2
        if [ "$APP_SIZE" -ge "$PLAT_SIZE" ]; then
            APP_STREAMS=$BIG_STREAMS
            PLAT_STREAMS=$(( TOTAL_STREAMS - BIG_STREAMS ))
        else
            PLAT_STREAMS=$BIG_STREAMS
            APP_STREAMS=$(( TOTAL_STREAMS - BIG_STREAMS ))
        fi
    fi
fi

# ── Parallel download, extract whichever lands first ───────────────────────
# No prediction about which zip finishes first — each download drops a
# sentinel file when it's done and we start that zip's extraction the
# moment its sentinel appears. When the two land together (the common
# case on a fast link) this degrades to "extract both at the end", which
# is what the script did before; when they separate, the early one's
# extraction is free.
#
# Sentinels rather than `wait -n`: that's bash 5.1+ for `-p`, and this
# script also runs under the macOS overlay job.
echo "[artifacts] Downloading app + platform in parallel (app=$APP_STREAMS platform=$PLAT_STREAMS range streams)..."
T0=$(_ms)
( _ranged_download "$APP_URL"      "$TMPDIR_DL/app.zip"      "$APP_STREAMS"  \
    && : > "$TMPDIR_DL/app.ok" || : > "$TMPDIR_DL/app.fail" ) &
( _ranged_download "$PLATFORM_URL" "$TMPDIR_DL/platform.zip" "$PLAT_STREAMS" \
    && : > "$TMPDIR_DL/platform.ok" || : > "$TMPDIR_DL/platform.fail" ) &

_zip_path()  { [ "$1" = app ] && echo "$TMPDIR_DL/app.zip"  || echo "$TMPDIR_DL/platform.zip"; }
_zip_dest()  { [ "$1" = app ] && echo "$DEST/app"           || echo "$DEST/platform"; }

EXTRACT_PIDS=""
PENDING="app platform"
while [ -n "$PENDING" ]; do
    STILL_PENDING=""
    STARTED=""
    for name in $PENDING; do
        if [ -e "$TMPDIR_DL/$name.fail" ]; then
            echo "[artifacts] ERROR: $name artifact download failed"
            exit 1
        fi
        if [ -e "$TMPDIR_DL/$name.ok" ]; then
            echo "[artifacts] ${name} downloaded ($(du -h "$(_zip_path "$name")" | cut -f1)) at $(( $(_ms) - T0 ))ms — extracting"
            _extract_zip "$(_zip_path "$name")" "$(_zip_dest "$name")" &
            EXTRACT_PIDS="$EXTRACT_PIDS $!"
            STARTED="$STARTED $name"
        else
            STILL_PENDING="$STILL_PENDING $name"
        fi
    done
    PENDING=$(echo "$STILL_PENDING" | tr -s ' ' | sed 's/^ //;s/ $//')
    [ -n "$PENDING" ] && [ -z "$STARTED" ] && sleep 0.5
done

T_DOWNLOADED=$(_ms)
APP_BYTES=$(stat -c%s "$TMPDIR_DL/app.zip")
PLAT_BYTES=$(stat -c%s "$TMPDIR_DL/platform.zip")
TOTAL_MB=$(( (APP_BYTES + PLAT_BYTES) / 1024 / 1024 ))
DOWNLOAD_MS=$(( T_DOWNLOADED - T0 ))
# Avoid divide-by-zero if somehow instantaneous
SPEED_MBS=$(( DOWNLOAD_MS > 0 ? TOTAL_MB * 1000 / DOWNLOAD_MS : 0 ))
echo "[artifacts] Downloaded: app=$(du -h "$TMPDIR_DL/app.zip" | cut -f1) platform=$(du -h "$TMPDIR_DL/platform.zip" | cut -f1) in ${DOWNLOAD_MS}ms (~${SPEED_MBS} MB/s)"

# ── Wait out the extractions ───────────────────────────────────────────────
# Whatever is left here started when its zip landed. On a fast link both
# start at roughly the same moment and this is the whole extraction cost;
# on a slow one the early zip is already most of the way through.
T_EXTRACT=$(_ms)
for pid in $EXTRACT_PIDS; do
    wait "$pid" || { echo "[artifacts] ERROR: artifact extraction failed"; exit 1; }
done
PLATFORM_VERSION=$(python3 -c "import json; print(json.load(open('$DEST/app/manifest.json'))['platform'])" 2>/dev/null)
echo "[artifacts] Platform version: $PLATFORM_VERSION"

# Stamp LAST, and only once both extractions have been waited on — the stamp
# is what a later run trusts to skip all of the above, so it must never
# describe a tree that is still being written.
if ! _cache_intact "$DEST"; then
    echo "[artifacts] ERROR: extraction finished but $DEST is missing app/manifest.json or platform/ServiceTier"
    exit 1
fi
{
    echo "key=$CACHE_KEY"
    echo "request=$REQUEST_KEY"
} > "$DEST/$STAMP_NAME"

T_DONE=$(_ms)
EXTRACT_MS=$(( T_DONE - T_EXTRACT ))
TOTAL_MS=$(( T_DONE - T0 ))
echo "[artifacts] Extract tail ${EXTRACT_MS}ms | Total: ${TOTAL_MS}ms | Disk: $(du -sh "$DEST" | cut -f1)"

echo "[artifacts] Done. Artifacts at $DEST"
