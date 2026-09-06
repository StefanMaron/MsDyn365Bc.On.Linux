#!/usr/bin/env python3
"""Split a comma-separated codeunit id list into chunks the Test Runner
Extension's API can actually accept.

WHY THIS EXISTS
---------------
run-tests.sh sets the websocket runner's test suite up by POSTing the whole
codeunit id list to

    /api/custom/automation/v1.0/companies(<id>)/codeunitRunRequests

as one JSON string field. That field is `CodeunitIds: Text[2048]` on table
99903 (extensions/TestRunnerExtension/src/RunnerTable.al), so BC rejects the
POST outright once the joined list passes 2048 characters:

    HTTP 400 Application_StringExceededLength
    "The length of the string is 2051, but it must be less than or equal to
     2048 characters."

Nothing about that is a test failure — publishing and discovery both succeed,
and the run dies before a single test executes. It is a pure size ceiling, and
a growing test suite walks into it silently: a repository is fine at 2047
characters and red at 2049. StefanMaron/BusinessCentral.AL.Language.Tests hit
it on 2026-09-05: 342 test codeunits joined to 2051 characters.

The ceiling belongs to BC's field definition, so the fix is on this side:
split the list, set the suite up and run it once per chunk, and merge the
results. run-tests.sh does the running; this module does the splitting, and is
kept separate so the split can be tested without a BC container.

TOTALITY IS THE WHOLE POINT
---------------------------
A batching bug that drops a chunk does not look like a bug. It looks like a
green run with fewer tests in it — the exact false-green shape issue #57 was
about. So `split_ids` never returns without proving, on the values it is about
to return, that:

  * the chunks concatenate back to the input token list, in order, with
    nothing dropped, duplicated or reordered;
  * no chunk is empty;
  * no chunk exceeds the character budget, unless it is a single token that
    cannot be split any further (which is reported, not hidden).

`--self-test` runs those checks against known-good and known-BROKEN chunkings,
so the verifier is proven to reject the failure it exists to catch rather than
just proven to accept correct input. It needs no BC connection:

    python3 scripts/chunk-codeunit-ids.py --self-test

USAGE
-----
    python3 scripts/chunk-codeunit-ids.py --max-chars 1000 "60100,60101,60102"
    printf '%s' "$IDS" | python3 scripts/chunk-codeunit-ids.py --max-chars 1000

Prints one chunk per line on stdout. Exit 0 on success, 2 on bad input or a
failed internal check.
"""

from __future__ import annotations

import argparse
import sys

# BC's hard ceiling: the width of `CodeunitIds` on table 99903.
FIELD_LIMIT = 2048

# Default budget per request. Deliberately far below FIELD_LIMIT rather than
# just under it: the list grows every time somebody adds a test codeunit, and
# a fix that lands at 2047 characters is red again on the next merge. Halving
# the ceiling means the list has to DOUBLE before the chunk count moves, and
# an extra chunk costs one suite setup plus one runner connection — a few
# seconds against a leg measured in minutes. Cheap insurance, so buy plenty.
DEFAULT_MAX_CHARS = 1000


class ChunkError(ValueError):
    """Raised when the input cannot be split, or a self-check fails."""


def parse_tokens(raw: str) -> list[str]:
    """Split the raw list on commas, dropping empties and surrounding space.

    Tokens are kept verbatim otherwise: SetupSuite accepts both bare ids
    ("60100") and ranges ("60100-60120"), and this must not reinterpret
    either — it only decides where the commas that separate requests go.
    """
    return [t.strip() for t in raw.split(",") if t.strip()]


def verify_chunks(tokens: list[str], chunks: list[str], max_chars: int) -> None:
    """Raise ChunkError unless `chunks` is a faithful, bounded split of `tokens`.

    Kept as a free function taking both sides so the self-test can feed it a
    deliberately broken chunking and confirm it objects.
    """
    if not chunks:
        raise ChunkError("split produced no chunks at all")

    rebuilt: list[str] = []
    for i, chunk in enumerate(chunks):
        if not chunk:
            raise ChunkError(f"chunk {i + 1} is empty")
        if chunk != chunk.strip():
            raise ChunkError(f"chunk {i + 1} has leading/trailing whitespace")
        parts = chunk.split(",")
        if any(p == "" for p in parts):
            raise ChunkError(f"chunk {i + 1} contains an empty id: {chunk!r}")
        if len(chunk) > max_chars and len(parts) > 1:
            raise ChunkError(
                f"chunk {i + 1} is {len(chunk)} chars, over the {max_chars}-char "
                f"budget, and holds {len(parts)} ids so it could have been split"
            )
        rebuilt.extend(parts)

    if rebuilt != tokens:
        # Say WHICH way it diverged; "not equal" on two 341-element lists is
        # not a diagnosis.
        if len(rebuilt) != len(tokens):
            raise ChunkError(
                f"chunks carry {len(rebuilt)} id(s) but the input had "
                f"{len(tokens)} — {abs(len(tokens) - len(rebuilt))} "
                f"{'lost' if len(rebuilt) < len(tokens) else 'invented'}"
            )
        first = next(i for i, (a, b) in enumerate(zip(rebuilt, tokens)) if a != b)
        raise ChunkError(
            f"chunks reorder or alter the id list: position {first} is "
            f"{rebuilt[first]!r}, input had {tokens[first]!r}"
        )


def split_ids(raw: str, max_chars: int = DEFAULT_MAX_CHARS) -> list[str]:
    """Split `raw` into comma-joined chunks of at most `max_chars` characters.

    Chunks are contiguous slices in input order, so the resulting execution
    order is the same as an un-chunked run's.
    """
    if max_chars < 1:
        raise ChunkError(f"max_chars must be positive, got {max_chars}")

    tokens = parse_tokens(raw)
    if not tokens:
        raise ChunkError("no codeunit ids to split")

    chunks: list[str] = []
    current = ""
    for token in tokens:
        candidate = f"{current},{token}" if current else token
        if current and len(candidate) > max_chars:
            chunks.append(current)
            current = token
        else:
            current = candidate
    if current:
        chunks.append(current)

    # A single token wider than the whole budget cannot be split; it goes out
    # on its own and is called out, because the caller's POST may still be
    # rejected and a silent pass-through would hide why.
    for chunk in chunks:
        if len(chunk) > max_chars and "," not in chunk:
            print(
                f"WARN: codeunit id token {chunk!r} is {len(chunk)} chars, longer "
                f"than the {max_chars}-char budget on its own — sending it alone",
                file=sys.stderr,
            )

    verify_chunks(tokens, chunks, max_chars)
    return chunks


# --------------------------------------------------------------------------
# Self-test — no BC connection, no network, runs in milliseconds.
# --------------------------------------------------------------------------

def self_test() -> int:
    failures: list[str] = []

    def check(label: str, cond: bool) -> None:
        print(("  PASS  " if cond else "  FAIL  ") + label)
        if not cond:
            failures.append(label)

    def raises(fn) -> bool:
        try:
            fn()
        except ChunkError:
            return True
        except Exception:  # noqa: BLE001 - any other error is still a failure
            return False
        return False

    print("split_ids:")

    # 342 five-digit ids join to 2051 characters — the exact list length that
    # took the AL-language corpus red on 2026-09-05, three characters over.
    ids = [str(60000 + i) for i in range(342)]
    raw = ",".join(ids)
    check("the real-world case is over BC's own field limit (this is the bug)",
          len(raw) == 2051 and len(raw) > FIELD_LIMIT)

    chunks = split_ids(raw, 1000)
    check("342 ids at 1000 chars produce more than one chunk", len(chunks) > 1)
    check("every chunk fits the budget", all(len(c) <= 1000 for c in chunks))
    check("every chunk fits BC's field limit too",
          all(len(c) <= FIELD_LIMIT for c in chunks))
    check("chunks rejoin to exactly the input, in order",
          ",".join(chunks).split(",") == ids)
    check("no chunk is empty", all(c for c in chunks))

    # Boundary: a budget that lands exactly on a separator, and one either side.
    exact = split_ids("111,222,333", 11)   # "111,222,333" is 11 chars
    check("a list exactly at the budget stays one chunk", exact == ["111,222,333"])
    check("one char under the budget splits", split_ids("111,222,333", 10) ==
          ["111,222", "333"])
    check("a tiny budget degrades to one id per chunk",
          split_ids("111,222,333", 3) == ["111", "222", "333"])

    # Ranges are tokens like any other and must survive verbatim.
    check("range tokens are preserved, not expanded",
          split_ids("60100-60120,60200", 12) == ["60100-60120", "60200"])

    # Whitespace and stray separators in, clean tokens out.
    check("whitespace and empty fields are normalised away",
          split_ids(" 111 , ,222,\n333 ", 1000) == ["111,222,333"])

    check("empty input is an error, not an empty run",
          raises(lambda: split_ids("", 1000)))
    check("a comma-only input is an error too",
          raises(lambda: split_ids(" , , ", 1000)))
    check("a non-positive budget is an error",
          raises(lambda: split_ids("111,222", 0)))

    # An id longer than the budget cannot be split — it must still come out,
    # exactly once, rather than being dropped to satisfy the bound.
    over = split_ids("1234567890,7", 4)
    check("an oversized single token is emitted rather than dropped",
          ",".join(over).split(",") == ["1234567890", "7"])

    print("\nverify_chunks rejects broken chunkings:")

    good = ["111,222", "333"]
    toks = ["111", "222", "333"]
    check("the good chunking is accepted",
          not raises(lambda: verify_chunks(toks, good, 10)))
    # These are the mutations that would ship as a green run with fewer tests.
    check("a DROPPED last chunk is rejected",
          raises(lambda: verify_chunks(toks, ["111,222"], 10)))
    check("a dropped id inside a chunk is rejected",
          raises(lambda: verify_chunks(toks, ["111", "333"], 10)))
    check("a DUPLICATED id is rejected",
          raises(lambda: verify_chunks(toks, ["111,222", "222,333"], 10)))
    check("REORDERED ids are rejected",
          raises(lambda: verify_chunks(toks, ["222,111", "333"], 10)))
    check("an empty chunk is rejected",
          raises(lambda: verify_chunks(toks, ["111,222", "", "333"], 10)))
    check("an over-budget splittable chunk is rejected",
          raises(lambda: verify_chunks(toks, ["111,222,333"], 10)))
    check("no chunks at all is rejected",
          raises(lambda: verify_chunks(toks, [], 10)))

    if failures:
        print(f"\n{len(failures)} check(s) failed: {', '.join(failures)}")
        return 1
    print("\nall checks passed")
    return 0


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(
        description="Split a comma-separated codeunit id list into "
                    "API-request-sized chunks.")
    ap.add_argument("ids", nargs="?", default=None,
                    help="comma-separated ids; read from stdin when omitted")
    ap.add_argument("--max-chars", type=int, default=DEFAULT_MAX_CHARS,
                    help=f"characters per chunk (default {DEFAULT_MAX_CHARS}; "
                         f"BC's field limit is {FIELD_LIMIT})")
    ap.add_argument("--self-test", action="store_true",
                    help="run the built-in checks and exit; needs no BC")
    args = ap.parse_args(argv)

    if args.self_test:
        return self_test()

    raw = args.ids if args.ids is not None else sys.stdin.read()
    try:
        for chunk in split_ids(raw, args.max_chars):
            print(chunk)
    except ChunkError as ex:
        print(f"ERROR: {ex}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
