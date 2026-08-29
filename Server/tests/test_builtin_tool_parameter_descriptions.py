"""PONT-12 -- a BUILT-IN tool's parameter descriptions must reach the schema an LLM reads.

⛔⛔ THIS FILE EXISTS BECAUSE PONT-09 READ ITS OWN MEASUREMENT BACKWARDS, AND THAT MISREADING SAT
IN A DOC-COMMENT FOR A DAY. Its header lists, as context::

    execute_code    6 parameters, 4 with a description     <- a BUILT-IN tool
    manage_scene   19 parameters, 1 with a description     <- a BUILT-IN tool

and concludes "the built-ins keep theirs -- so the failure is in the custom-tool path alone".
⭐ The two numbers in front of it said the opposite: 19 parameters and ONE sentence is not a tool
keeping its descriptions, it is a tool that has lost eighteen. The conclusion was true of the
CUSTOM path and false of the built-ins, and nothing in the sentence separated the two.

MEASURED 2026-08-29 ~23:0x on the RUNNING server (`tools/list` over HTTP, the very bytes an LLM
receives), by titulaire 6 (session 9fd89ce4):

    428 parameters over 46 tools,  158 with NO description at all  (36.9 %), over 17 tools
        manage_gameobject  27 / 27 anonymous          read_console   8 / 8 anonymous
        manage_ui          29 / 30                    manage_scene  18 / 19
    after the repair:                 0  (0.0 %)   -- 428 parameters still there, none lost

⭐⭐ AND THOSE ARE EXACTLY THE TOOLS THE WORKSHOP WAS MEASURED TO BYPASS BY HAND (`manage_ui`:
3 calls against 404 hand-written `execute_code` bodies). A tool whose 27 parameters are anonymous
is not a tool an LLM can choose to use.

TWO CAUSES, both of them one keystroke wide, and the second only appeared once the first was gone:

  ① `Annotated[T, "doc"] | None = None`   -- the union OUTSIDE the annotation.
     That is `Union[Annotated[T, "doc"], None]`: the schema generator builds an `anyOf` of two arms
     and the metadata hanging off one arm is dropped. `Annotated[T | None, "doc"] = None` keeps it.
     154 occurrences, rewritten by `/studio/pont-unity/outils/reparer-annotated-optionnel.py`
     (self-test: 8 cases, both edges).
  ② `Annotated[T, "doc A", "doc B"]`      -- TWO metadata strings.
     FastMCP cannot choose and emits neither. 4 occurrences in `manage_script`, merged by hand.

WHAT THIS FILE GUARDS, and why a ratchet rather than a review: both causes are invisible in the
source. `Annotated[str, "..."] | None = None` reads as perfectly documented code -- the sentence is
right there. Only the served schema says it never left the building. So the guard has to be here,
and it has to read the schema.

HOW TO PROVE THESE ASSERTIONS CAN REDDEN -- form ⓑ, embedded, nothing to replay by hand.
  `test_the_broken_form_loses_its_description` and `test_the_correct_form_keeps_it` are the two
  edges of the SAME sentence, on two functions identical but for the placement of `| None`. They
  assert a DIFFERENCE, so no fallback can satisfy both: a schema generator that dropped every
  description would redden the second, one that invented descriptions would redden the first.
  ⇒ if those two ever go green together, this file's central claim has changed and the ratchet
    below is measuring nothing -- read them before believing the ratchet.

  AND THE RATCHET ITSELF WAS PLAYED, not predicted -- 2026-08-29 ~23:4x, announced by NAME before
  running, because a count cannot tell a real failure from a noisy neighbour on a shared machine:
    MUT-E  `manage_editor.tool_name` put back into the broken form, that parameter and no other
           RED:   test_no_built_in_parameter_reaches_an_llm_anonymous, naming `manage_editor.tool_name`
                  and nothing else -- "1 built-in parameters ... out of 416"
           GREEN: the three calibration tests above
           restored, and the four went green again.
  ⚠ Two earlier shapes of this file were GREEN BY BLINDNESS and both were caught by a floor
    assertion rather than by reading the code: the wrong `Tool` class (411 of 416 "anonymous" while
    the running server served 0 of 428), and the stubbed decor (0 parameters inspected). Keep the
    `seen >= 100` floor: it is the only thing standing between this ratchet and a clean-looking zero.
"""
from typing import Annotated

import contextlib
import pathlib
import sys

import pytest


# ⛔⛔ THE INSTRUMENT IS LOAD-BEARING, AND THE WRONG ONE WAS TRIED FIRST — CAUGHT BY THE PAIR BELOW.
# `mcp.server.fastmcp.tools.Tool` is a DIFFERENT schema builder from the one this server runs on
# (`fastmcp` v2, which every tool module imports its `Context` from). Asked with the wrong one, the
# probe returned "411 of 416 parameters anonymous" while the RUNNING server served 0 of 428 — it
# would have sent someone to repair code that is fine. It was caught only because
# `test_the_correct_form_keeps_it` reddened too: a judge that only checks the defect is green by
# blindness. ⇒ build the schema with the SAME instrument the subject reads, never a cousin of it.
#
# ⚠ AND THE REAL ONE HAS TO BE ASKED FOR. `tests/integration/conftest.py` puts a STUB `fastmcp` in
# `sys.modules` at collection time; importing `fastmcp.tools` at module level then dies with
# "'fastmcp' is not a package" — green alone, broken in the suite. So the real package is loaded
# lazily, and if it cannot be, this file says THAT rather than blaming a description.
@contextlib.contextmanager
def _real_fastmcp():
    """Lend us the real `fastmcp`, then put the stub back exactly as it was.

    ⛔⛔ RESTORING IS NOT POLITENESS, IT IS THE WHOLE OF IT. A first version deleted the stub and
    left the real package in `sys.modules`: this file went green and **39 integration tests
    failed**, with `ImportError: cannot import name 'FastMCP' from 'fastmcp.server.server'` — a
    judge that breaks its neighbours' decor and blames their law. `sys.modules` is shared state,
    and a test that mutates it owns putting it back.
    """
    import importlib

    saved = {name: module for name, module in sys.modules.items()
             if name == "fastmcp" or name.startswith("fastmcp.")}
    for name in saved:
        del sys.modules[name]
    try:
        tools = importlib.import_module("fastmcp.tools")
        assert hasattr(tools, "Tool"), (
            "`fastmcp.tools.Tool` is not available even after setting the stub aside. Do NOT read "
            "any failure in this file as a description defect — the decor is broken."
        )
        yield tools.Tool
    finally:
        for name in [n for n in sys.modules
                     if n == "fastmcp" or n.startswith("fastmcp.")]:
            del sys.modules[name]
        sys.modules.update(saved)


def _schema_of(function) -> dict:
    with _real_fastmcp() as tool:
        return tool.from_function(function, name="probe").parameters.get("properties", {})


# --------------------------------------------------------------------------------------------
# The embedded calibration: two functions, one keystroke apart, asserted to DIFFER.
# --------------------------------------------------------------------------------------------

def _broken(spare: Annotated[int, "a sentence written for a reader"] | None = None) -> None:
    """The form that was written 154 times across the built-in tools."""


def _correct(spare: Annotated[int | None, "a sentence written for a reader"] = None) -> None:
    """The same parameter, the same sentence, the union moved inside."""


def test_the_broken_form_loses_its_description():
    """The defect itself, held in place so nobody has to trust a story about it."""
    assert not _schema_of(_broken)["spare"].get("description"), (
        "`Annotated[T, 'doc'] | None` now KEEPS its description -- either the schema generator "
        "changed, or this file's whole premise did. Do not silence this: re-read the ratchet."
    )


def test_the_correct_form_keeps_it():
    """The other edge, and the pair is what makes either one worth reading."""
    assert _schema_of(_correct)["spare"].get("description") == "a sentence written for a reader"


def test_the_two_forms_disagree():
    """Asserted as a DIFFERENCE: a generator that dropped -- or invented -- every description
    would satisfy one of the two tests above and be caught here."""
    assert _schema_of(_broken)["spare"].get("description") != \
        _schema_of(_correct)["spare"].get("description")


# --------------------------------------------------------------------------------------------
# The ratchet, over the real built-in tools.
# --------------------------------------------------------------------------------------------

_PROBE = r"""
import json, sys, importlib, pkgutil, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "src"))
from fastmcp.tools import Tool
import services.tools as tools_package
from services.registry import get_registered_tools

for module in pkgutil.iter_modules(tools_package.__path__):
    if not module.name.startswith("_"):
        importlib.import_module("services.tools." + module.name)

seen, mute = 0, []
for entry in get_registered_tools():
    function = entry.get("func") or entry.get("function")
    if function is None:
        continue
    try:
        properties = Tool.from_function(function, name="probe").parameters.get("properties", {})
    except Exception as error:      # a tool whose schema cannot be built is a DIFFERENT defect,
        mute.append(entry["name"] + ".<schema failed: " + type(error).__name__ + ">")
        continue                    # and it is reported, never swallowed
    for name, spec in properties.items():
        if name == "ctx":
            continue
        seen += 1
        if not spec.get("description"):
            mute.append(entry["name"] + "." + name)
print(json.dumps({"seen": seen, "mute": mute}))
"""


def _measure_built_ins():
    """Run the measurement in a CLEAN interpreter, and never in this one.

    ⛔⛔ WHY A SUBPROCESS, measured rather than chosen for elegance. `tests/integration/conftest.py`
    stubs `fastmcp` at collection time, so by the time this file runs, every tool module has been
    imported against a DOUBLE `Context`. Building their schemas with the real `fastmcp` then throws
    for all of them: run alone this file saw 418 parameters, run in the suite it saw **0** — and
    zero mute parameters out of zero inspected reads exactly like a clean bill of health.
    ⭐ The `seen >= 100` floor below is what caught it, and it is the only reason this note exists.
    A fresh interpreter has no stub, so the measurement means the same thing either way.
    """
    import json
    import subprocess

    # the probe derives src/ from its own location, so it must sit beside this file
    script = pathlib.Path(__file__).resolve().parent / "_pont12_probe.py"
    script.write_text(_PROBE, encoding="utf-8")
    try:
        completed = subprocess.run(
            [sys.executable, str(script)], capture_output=True, text=True, timeout=180)
    finally:
        script.unlink(missing_ok=True)
    assert completed.returncode == 0, (
        "the probe interpreter failed to run at all — read this as a broken decor, not as a "
        f"description defect:\n{completed.stderr[-2000:]}"
    )
    return json.loads(completed.stdout.strip().splitlines()[-1])


def test_no_built_in_parameter_reaches_an_llm_anonymous():
    result = _measure_built_ins()
    seen, mute = result["seen"], result["mute"]

    assert seen >= 100, (
        f"only {seen} parameters were inspected; the probe is not reading the built-ins at all "
        "and its zero below would be a false green."
    )
    assert not mute, (
        f"{len(mute)} built-in parameters reach the LLM with no description, out of {seen}.\n"
        "Both known causes are one keystroke wide — see this file's header:\n"
        "  `Annotated[T, 'doc'] | None = None`  ->  `Annotated[T | None, 'doc'] = None`\n"
        "  `Annotated[T, 'doc A', 'doc B']`     ->  a single merged sentence\n"
        + "\n".join(f"  {name}" for name in sorted(mute))
    )
