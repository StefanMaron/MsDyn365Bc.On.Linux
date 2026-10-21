#!/usr/bin/env python3
"""Regression test for scripts/classify-handler-codeunits.py routing.

Each case is a small AL test codeunit and the runner it must be routed to.
It needs no BC and runs in under a second.

The case that matters most is issue #93: a Confirm in a part's OnOpenPage with
no handler bound, asserted with `Assert.ExpectedError('Unhandled UI: Confirm')`.
The test has no `.RunModal(` or `.Invoke(` call, so the older rules did not see
it, it went to the altool hub, and the hub (which has no test runner codeunit to
refuse the Confirm) opened the page silently.
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import importlib.util

spec = importlib.util.spec_from_file_location(
    "classify", os.path.join(HERE, "classify-handler-codeunits.py")
)
classify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(classify)


def codeunit(cuid: int, body: str, permissions: str = "Disabled") -> str:
    return f"""codeunit {cuid} "Case {cuid}"
{{
    Subtype = Test;
    TestPermissions = {permissions};

    [Test]
    procedure T()
    var
        Host: TestPage "Some Page";
    begin
{body}
    end;
}}
"""


CASES = [
    # (id, body, permissions, expected needs_websocket, why)
    (1, "        asserterror Host.OpenView();\n        Assert.ExpectedError('Unhandled UI: Confirm');",
     "Disabled", True, "issue #93: names the Unhandled UI text, no modal call"),
    (2, "        asserterror Host.OpenView();\n        Assert.ExpectedError('unhandled ui');",
     "Disabled", True, "text match is case-insensitive"),
    (3, "        Host.OpenView();\n        Assert.AreEqual('x', Host.Field.Value, '');",
     "Disabled", False, "plain TestPage use stays on the fast path"),
    (4, "        asserterror Host.OpenView();\n        Assert.ExpectedError('Some other error');",
     "Disabled", False, "asserterror on an open without the Unhandled UI text stays fast"),
    (5, "        asserterror Host.Part.Invoke();\n        Assert.ExpectedError('x');",
     "Disabled", True, "rule (b) still fires"),
    (6, "        Host.OpenView();",
     "Restrictive", True, "rule (c) still fires"),
    (7, "        asserterror NumberSequence.Insert('X');\n        Assert.ExpectedError('already exists');",
     "Disabled", True, "issue #120: a sequence error under asserterror"),
    (8, "        NumberSequence.Insert('X');\n        NumberSequence.Delete('X');",
     "Disabled", False, "a sequence without asserterror stays fast"),
    (9, "        asserterror Error('x');\n        Assert.ExpectedError('x');",
     "Disabled", False, "asserterror without a sequence stays fast"),
    (10, "        asserterror RR.Open(80301);\n        Assert.ExpectedError('from the object Test Runner - Mgt. An object');",
     "Disabled", True, "issue #140: names the test runner codeunit"),
    (11, "        asserterror RR.Open(80301);\n        Assert.ExpectedErrorCode('AL:MetadataNotFound');",
     "Disabled", False, "the same open without the runner's name stays fast"),
]


PREPROCESSOR_CASES = [
    # (symbols, expected needs_websocket, why)
    (None, True, "no symbols: every branch is scanned"),
    ({"BC28PLUS"}, False, "the BC 27 #else branch is not compiled, its runner name does not count"),
    (set(), True, "no symbol set: the #else branch is the compiled one"),
    ({"BC28PLUS", "BC27PLUS"}, False, "#if with && and !, nested"),
]

PREPROCESSOR_SOURCE = """codeunit 90166 "Case 90166"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure T()
    begin
#if BC28PLUS
        Expect('from the object Case 90166.');
#else
        Expect('from the object Test Runner - Mgt. An object');
#endif
#if BC27PLUS && !BC28PLUS
        Expect('Test Runner - Isol. Disabled');
#endif
    end;
}
"""


def preprocessor_cases() -> int:
    failures = 0
    with tempfile.TemporaryDirectory() as d:
        with open(os.path.join(d, "90166.al"), "w") as f:
            f.write(PREPROCESSOR_SOURCE)
        for symbols, expected, why in PREPROCESSOR_CASES:
            info = classify.classify_al_source([d], symbols)
            got = info.get(90166, {}).get("needs_websocket")
            ok = got is expected
            print(f"{'ok  ' if ok else 'FAIL'} #if symbols={symbols}: needs_websocket={got} (want {expected}) — {why}")
            failures += 0 if ok else 1
    return failures


def main() -> int:
    failures = 0
    with tempfile.TemporaryDirectory() as d:
        for cuid, body, perms, _, _ in CASES:
            with open(os.path.join(d, f"{cuid}.al"), "w") as f:
                f.write(codeunit(cuid, body, perms))
        info = classify.classify_al_source([d])
        for cuid, _, _, expected, why in CASES:
            got = info.get(cuid, {}).get("needs_websocket")
            ok = got is expected
            print(f"{'ok  ' if ok else 'FAIL'} cu {cuid}: needs_websocket={got} (want {expected}) — {why}")
            failures += 0 if ok else 1
    print(f"{len(CASES) - failures}/{len(CASES)} cases passed")
    pre_failures = preprocessor_cases()
    return 1 if failures or pre_failures else 0


if __name__ == "__main__":
    sys.exit(main())
