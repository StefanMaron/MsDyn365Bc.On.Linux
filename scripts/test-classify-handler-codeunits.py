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
]


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
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
