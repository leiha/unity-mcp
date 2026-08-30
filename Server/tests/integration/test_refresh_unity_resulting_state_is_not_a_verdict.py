"""PONT-24 -- `resulting_state` carries a TRUE value under a name that promises another one.

`[MEASURED 2026-08-30 by the `pont` seat, read in the editor's own source rather than inferred:
 MCPForUnity/Editor/Tools/RefreshUnity.cs:148-150 computes the field from
 `EditorApplication.isCompiling` AT REPLY TIME, from the editor's GLOBAL state, and returns it on
 every call -- including one that asked for no compilation at all.]`

TEN SESSIONS SHARE THIS EDITOR, so somebody is nearly always compiling. The field therefore lies
in both directions, and the second direction is the expensive one:

    compile="none"     "compiling" reads as "my call started one".  It did not. A seat of this
                       workshop watched a reload counter for TEN MINUTES on that reading.
    compile="request"  "idle" reads as "mine finished".  It equally means "not started yet".
                       That is a false GREEN, and a false green gets acted upon.

⇒ The value is right; the NAME is the defect. A field that AFFIRMS is worse than one that stays
silent: silence gets checked, an affirmation does not.

⚠ AND THE REPAIR REPORTS, IT DOES NOT CONCLUDE. This server cannot know WHO is compiling and must
not say. The last pure case below is what forbids that drift -- naming a culprit would send
somebody to repair the wrong thing, which is the failure family this workshop pays for the most.

⭐ WHY THIS JUDGE HAS TWO FLOORS. `test_the_reading_reaches_a_real_caller` is not a duplicate of
the pure-function cases: those prove the SENTENCE, and a sentence nobody receives repairs nothing.
The wiring is a different grandeur from the wording, and no amount of severity on one reaches a
defect living in the other.

HOW TO PROVE THESE ASSERTIONS CAN REDDEN
  PLAYED 2026-08-30 by the `pont` seat -- what follows is what HAPPENED, not what was predicted.
  Instrument calibrated first (healthy state: 6 passed, no FAILED line) and the file compared byte
  for byte against its original afterwards. `--color=no` is required for the NAMES to be readable
  at all -- a count does not say WHICH.
  MUT-A  make the `compile="none"` branch emit the `compile="request"` wording
         announced RED: 2   MEASURED RED: 4   ⛔ THE CERTIFICATE UNDER-PROMISED -- see below.
                        did_not_ask_for_a_compilation · is_the_one_matching_what_was_asked
                        · reports_and_does_not_conclude · reaches_a_real_caller
  MUT-B  drop the `"resulting_state" not in data` guard (annotate anything)
         announced RED: a_reply_without_the_field_comes_back_untouched   MEASURED: it alone. exact
  MUT-C  let the reading name a culprit ("a neighbour IS compiling right now")
         announced RED: the_repair_reports_and_does_not_conclude          MEASURED: it alone. exact
  MUT-D  skip the annotate call at the wiring site, keep the function intact
         announced RED: the_reading_reaches_a_real_caller                 MEASURED: it alone. exact
  ⚠ Four passes, four DIFFERENT measured sets. A neighbouring chain degrades at random; it does not
  reproduce four distinct sets, three of them named exactly in advance.

  ⭐⭐ WHY MUT-A's TWO EXTRA REDS ARE WRITTEN DOWN INSTEAD OF QUIETLY ENJOYED. A certificate that
  under-promises produces no unpleasant surprise -- one reads "4 failed" against 3 announced and
  hears good news -- so nobody ever re-reads it. The cost is real: a guard that is not listed here
  looks redundant to the next editor, who deletes it "cleaning up". The cause, once looked at, is
  plain: MUT-A does not merely swap wording, it removes the `compile="none"` BRANCH ENTIRELY, and
  the reserve sentence plus the wiring case both live downstream of that branch. So MUT-A is a
  COARSER mutation than its name suggests -- it is MUT-C's and MUT-D's superset by construction.
  ⇒ MUT-C and MUT-D are what carry the discriminating power here; MUT-A alone would not separate
  the wording defect from the reserve defect.

  ⭐ AND MUT-D IS THE ONE WORTH KEEPING. It leaves the function perfectly correct and cuts only the
  call site: five cases stay green while the wiring case alone falls. That is the direct proof that
  the two floors of this judge read DIFFERENT grandeurs -- and that a lot can be PROVED and FALSE
  at the same time when its judge and its defect do not live in the same room.
"""
import pytest

from services.tools.refresh_unity import annotate_resulting_state

from .test_helpers import DummyContext


def _reply(state: str = "compiling") -> dict:
    """A reply shaped like the editor's, carrying the field under test."""
    return {"success": True, "message": "Refresh requested.",
            "data": {"refresh_triggered": True, "resulting_state": state}}


def test_the_reading_says_this_call_did_not_ask_for_a_compilation():
    out = annotate_resulting_state(_reply("compiling"), "none")
    reading = out["data"].get("resulting_state_reading", "")

    # Decor proved before the claim: without the field there is nothing to annotate, and every
    # assertion below would pass or fail for a reason unrelated to the wording.
    assert out["data"]["resulting_state"] == "compiling", (
        "The original field must survive: callers read it, and this repair adds, never replaces."
    )
    assert "compile='none'" in reading, (
        "The whole defect is that a call which requested NO compilation answers 'compiling'. "
        "If the reading does not say what THIS call asked for, it explains nothing."
    )
    assert "domain_state" in reading or "read_console" in reading, (
        "A refusal that does not name what unblocks it does not expose that capability at all. "
        "Ten minutes were spent watching a counter that was never going to move."
    )


def test_the_reading_warns_that_idle_is_not_finished():
    reading = annotate_resulting_state(_reply("idle"), "request")["data"]["resulting_state_reading"]

    assert "not compiling AT THIS SECOND" in reading, (
        "⛔ The false GREEN, and it is the expensive direction. 'idle' after compile='request' is "
        "equally true BEFORE the compilation starts. A caller who reads it as 'done' builds on a "
        "domain that never swapped."
    )


def test_the_reading_is_the_one_matching_what_was_asked():
    none_reading = annotate_resulting_state(_reply(), "none")["data"]["resulting_state_reading"]
    req_reading = annotate_resulting_state(_reply(), "request")["data"]["resulting_state_reading"]

    assert none_reading != req_reading, (
        "Two decors that differ ONLY on the value under test must give DIFFERENT answers. A single "
        "case can always be re-tuned back to green; an ASSERTED GAP cannot -- a silent fallback to "
        "one generic sentence would satisfy either case alone, and fails here."
    )


def test_the_repair_reports_and_does_not_conclude():
    reading = annotate_resulting_state(_reply("compiling"), "none")["data"]["resulting_state_reading"]

    assert "CANNOT tell you whose it is" in reading, (
        "⛔ This is what forbids the drift. The server cannot know WHO is compiling. Naming a "
        "suspect is useful; naming a cause sends somebody to repair the wrong thing -- so the "
        "explicit reserve must stay in the text."
    )


def test_a_reply_without_the_field_comes_back_untouched():
    plain = {"success": False, "message": "boom", "data": {"timeout": True}}
    assert annotate_resulting_state(plain, "none") is plain
    assert "resulting_state_reading" not in plain["data"], (
        "Totality is not decoration: refresh_unity has several return paths, and one that carries "
        "no such field must pass through unharmed or the repair breaks the error replies."
    )
    assert annotate_resulting_state(None, "none") is None


@pytest.mark.asyncio
async def test_the_reading_reaches_a_real_caller(monkeypatch):
    """The SECOND FLOOR: the sentence is right AND it is actually wired in.

    A judge that only exercises the pure function is a judge whose grandeur is the WORDING while
    the defect could live in the WIRING. Both are measured, or neither is proved.
    """
    import services.tools.refresh_unity as mod

    async def fake_send(*args, **kwargs):
        return _reply("compiling")

    async def instantly_ready(ctx, timeout_s=60.0):
        return True, 0.0

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send, raising=False)
    monkeypatch.setattr(mod, "wait_for_editor_ready", instantly_ready)

    resp = await mod.refresh_unity(DummyContext(), compile="none")

    data = resp.data if hasattr(resp, "data") else resp["data"]
    assert data.get("resulting_state") == "compiling", (
        "This case did not travel the path that carries the field: nothing below is measured."
    )
    assert "compile='none'" in (data.get("resulting_state_reading") or ""), (
        "The function is correct and the caller never sees it -- exactly the shape of a lot that "
        "is PROVED and FALSE at the same time."
    )
