import pytest

from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_run_tests_async_forwards_params(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(
        DummyContext(),
        mode="EditMode",
        test_names="MyNamespace.MyTests.TestA",
        include_details=True,
    )
    assert captured["command_type"] == "run_tests"
    assert captured["params"]["mode"] == "EditMode"
    assert captured["params"]["testNames"] == ["MyNamespace.MyTests.TestA"]
    assert captured["params"]["includeDetails"] is True
    assert resp.success is True
    assert resp.data is not None
    assert resp.data.job_id == "abc123"


@pytest.mark.asyncio
async def test_run_tests_forwards_init_timeout(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "PlayMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(
        DummyContext(),
        mode="PlayMode",
        init_timeout=120000,
    )
    assert captured["params"]["initTimeout"] == 120000
    assert resp.success is True


@pytest.mark.asyncio
async def test_run_tests_omits_init_timeout_when_none(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(DummyContext(), mode="EditMode")
    assert "initTimeout" not in captured["params"]
    assert resp.success is True


@pytest.mark.asyncio
async def test_run_tests_rejects_negative_init_timeout():
    from services.tools.run_tests import run_tests

    resp = await run_tests(DummyContext(), mode="EditMode", init_timeout=-1)
    assert resp.success is False
    assert "init_timeout" in resp.error


@pytest.mark.asyncio
async def test_run_tests_rejects_zero_init_timeout():
    from services.tools.run_tests import run_tests

    resp = await run_tests(DummyContext(), mode="EditMode", init_timeout=0)
    assert resp.success is False
    assert "init_timeout" in resp.error


@pytest.mark.asyncio
async def test_get_test_job_forwards_job_id(monkeypatch):
    from services.tools.run_tests import get_test_job

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "data": {"job_id": params["job_id"], "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-1")
    assert captured["command_type"] == "get_test_job"
    assert captured["params"]["job_id"] == "job-1"
    assert resp.success is True
    assert resp.data is not None
    assert resp.data.job_id == "job-1"


# ---------------------------------------------------------------------------
# PONT-22 -- the release for a stuck test job, and the refusal that hid it.
#
# `clear_stuck` was read by the editor (RunTests.cs:24) and declared nowhere on this side, so no
# MCP client could send it: the schema a caller sees is built from the annotated parameters alone.
# ⛔ It is the ONLY release for a test job stuck in `running`, and the preflight refuses on
# `tests_running` BEFORE anything reaches the editor -- so the one call that clears the lock was
# turned away by the lock it clears.
#
# HOW TO PROVE THESE ASSERTIONS CAN REDDEN
#   PLAYED 2026-08-30 08:5x, three mutations, each announced before its pass. Not a prediction:
#   what follows is what happened. Instrument calibrated first -- the healthy state prints no
#   FAILED line, and `--color=no` was required for the names to be readable at all (a first pass
#   read only counts, and a count does not say WHICH).
#   MUT-A  remove the `if clear_stuck:` early return (let the gate run first)
#          announced RED: test_clear_stuck_is_not_turned_away_by_the_lock_it_clears
#          MEASURED     : that case, and it alone.                              ✅ exact
#   MUT-B  drop the `tests_running` re-wording block
#          announced RED: ...names_clear_stuck AND ...left_alone
#          MEASURED     : ...names_clear_stuck ALONE.                           ⛔ OVER-ANNOUNCED
#          ⭐ The announcement was wrong, and the judge is right: with no re-wording at all, the
#          `compiling` message is handed back untouched -- which is exactly what ...left_alone
#          demands. A gap-assertion only reddens when the two branches COLLAPSE, never when the
#          feature is simply absent. Written here rather than quietly fixed: an announcement that
#          claims MORE than it gets is caught on the first run; the reverse never is.
#   MUT-C  re-word every busy refusal, whatever its reason
#          announced RED: test_a_busy_for_another_reason_is_left_alone, and it ALONE
#          MEASURED     : that case, and it alone.                              ✅ exact
#   ⚠ The three passes reddened three DIFFERENT single cases, two of them named in advance. Noise
#   from a neighbouring chain degrades at random; it does not reproduce three distinct sets.
# ---------------------------------------------------------------------------


def _busy_gate(reason: str):
    """A preflight that always refuses, the way a held lock does."""
    from models import MCPResponse

    async def _gate(ctx, **kwargs):
        return MCPResponse(
            success=False,
            error="busy",
            message=reason,
            hint="retry",
            data={"reason": reason, "retry_after_ms": 5000},
        )
    return _gate


@pytest.mark.asyncio
async def test_clear_stuck_is_not_turned_away_by_the_lock_it_clears(monkeypatch):
    from services.tools.run_tests import run_tests
    import services.tools.run_tests as mod

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "message": "Stuck job cleared.", "data": {"cleared": True}}

    monkeypatch.setattr(mod.unity_transport,
                        "send_with_unity_instance", fake_send_with_unity_instance)
    # The decor IS the defect: a gate that refuses exactly as a held lock does. If the request
    # went through it, nothing would ever reach the editor.
    monkeypatch.setattr(mod, "preflight", _busy_gate("tests_running"))

    resp = await run_tests(DummyContext(), clear_stuck=True)

    assert captured, (
        "Nothing reached the editor: the release for a stuck job was refused by the lock it "
        "exists to clear."
    )
    assert captured["command_type"] == "run_tests"
    assert captured["params"] == {"clear_stuck": True}, (
        "A clear_stuck request starts no tests: it must carry that flag and nothing else."
    )
    assert resp["success"] is True


@pytest.mark.asyncio
async def test_a_tests_running_refusal_names_clear_stuck(monkeypatch):
    from services.tools.run_tests import run_tests
    import services.tools.run_tests as mod

    monkeypatch.setattr(mod, "preflight", _busy_gate("tests_running"))

    resp = await run_tests(DummyContext(), mode="EditMode")

    assert resp.success is False
    assert "clear_stuck" in (resp.message or ""), (
        "A refusal that does not name what unblocks it does not expose that capability: the "
        "caller reads `retry`, retries the identical call, and loops."
    )
    assert "get_test_job" in (resp.message or ""), (
        "And it must point at the way to check that no run is genuinely in flight -- otherwise "
        "the advice invites killing a live run."
    )


@pytest.mark.asyncio
async def test_a_busy_for_another_reason_is_left_alone(monkeypatch):
    from services.tools.run_tests import run_tests
    import services.tools.run_tests as mod

    # Identical in every respect except the value under test. A collapse to a single wording --
    # re-writing every busy, or none -- cannot satisfy this case and the one above at the same time.
    monkeypatch.setattr(mod, "preflight", _busy_gate("compiling"))

    resp = await run_tests(DummyContext(), mode="EditMode")

    assert resp.success is False
    assert resp.message == "compiling", (
        "A refusal for another reason must be handed back untouched: clear_stuck answers a held "
        "lock, never a compiling editor, and advertising it there would send a caller to break "
        "something that is merely busy."
    )
