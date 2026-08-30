"""PONT-23 -- the timeout of refresh_unity, and the two moves it never named.

`[MEASURED 2026-08-30 08:2x-08:4x by the `pont` seat, with three other seats waiting behind it:
 an import was requested, this timeout came back, and the seat then watched a domain-reload
 counter for TEN MINUTES that was never going to move. One line of the editor log said exactly
 why -- a compile error in a neighbour's test assembly. The reply pointed at neither the log nor
 the flag.]`

⇒ A refusal that does not name what unblocks it does not expose that capability at all. The caller
does the only thing left to him: he waits, or he fires the identical call again.

⚠ AND THE REPAIR REPORTS, IT DOES NOT CONCLUDE. A timeout cannot tell a blocking compile error
from a reload that is merely long -- this workshop measures reloads at 2 to 9 minutes. Naming
where to look is useful; naming a cause would send somebody to repair the wrong thing. The last
case below is what forbids that drift.

HOW TO PROVE THESE ASSERTIONS CAN REDDEN
  PLAYED 2026-08-30 09:0x. Not a prediction -- what follows is what happened, instrument
  calibrated first (the healthy state prints no FAILED line; `--color=no` is required for the
  names to be readable at all -- a first pass elsewhere read only counts, and a count does not
  say WHICH).
  MUT-A  drop the words `read_console` from the message
         announced RED: test_the_timeout_sends_the_caller_to_the_console
         MEASURED     : that case, and it alone.                              exact
  MUT-B  drop the words `wait_for_ready` from the message
         announced RED: test_the_timeout_names_the_flag_that_avoids_the_wait
         MEASURED     : that case, and it alone.                              exact
  MUT-C  harden the wording into a CAUSE (remove the innocent explanation)
         announced RED: test_the_timeout_reports_and_does_not_conclude
         MEASURED     : that case, and it alone.                              exact
  ⚠ Three passes, three DIFFERENT single cases, all three named in advance. Noise from a
  neighbouring chain degrades at random; it does not reproduce three distinct sets.
  ⭐ MUT-C is the one worth keeping: it proves the judge defends the RESERVE, not just the advice.
  A later editor who tightens "cannot tell apart" into "this is why" makes it red.
"""
import pytest

from .test_helpers import DummyContext


async def _timing_out_refresh(monkeypatch):
    """Drives refresh_unity down its timeout path and returns the reply."""
    import services.tools.refresh_unity as mod

    async def fake_send(*args, **kwargs):
        return {"success": True, "data": {}}

    async def never_ready(ctx, timeout_s=60.0):
        return False, None

    monkeypatch.setattr(mod.unity_transport,
                        "send_with_unity_instance", fake_send, raising=False)
    monkeypatch.setattr(mod, "wait_for_editor_ready", never_ready)
    return await mod.refresh_unity(DummyContext(), wait_for_ready=True)


@pytest.mark.asyncio
async def test_the_timeout_sends_the_caller_to_the_console(monkeypatch):
    resp = await _timing_out_refresh(monkeypatch)

    assert resp.success is False
    # The decor is proved before the claim: if the timeout path were not the one taken, the
    # assertions below would pass or fail for a reason that has nothing to do with the message.
    assert resp.data.get("timeout") is True, (
        "This case did not reach the timeout path: nothing below has been measured."
    )
    assert "read_console" in (resp.message or ""), (
        "A blocking compile error is reported in the console and NOWHERE in this reply. Without "
        "the pointer, the caller waits on a reload that will never come -- ten minutes, measured."
    )


@pytest.mark.asyncio
async def test_the_timeout_names_the_flag_that_avoids_the_wait(monkeypatch):
    resp = await _timing_out_refresh(monkeypatch)

    assert "wait_for_ready" in (resp.message or ""), (
        "`wait_for_ready=False` is a real parameter of this same tool. A refusal that does not "
        "name it leaves the caller to sit through 60s on every single attempt."
    )


@pytest.mark.asyncio
async def test_the_timeout_reports_and_does_not_conclude(monkeypatch):
    resp = await _timing_out_refresh(monkeypatch)
    message = resp.message or ""

    assert "NOT that the refresh failed" in message, (
        "A timeout waiting for readiness is not a failed refresh, and a caller who reads it as "
        "one re-fires an import that already landed."
    )
    assert "2 to 9 minutes" in message, (
        "⛔ This is what forbids the drift. The tool cannot tell a blocking compile error from a "
        "reload that is merely long. Naming a suspect is useful; naming a cause would send "
        "somebody to repair the wrong thing -- so the innocent explanation must stay in the text."
    )
