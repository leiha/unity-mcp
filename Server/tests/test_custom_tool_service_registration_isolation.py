"""PONT-07 — one malformed custom tool must not sink the ones registered after it.

WHY THIS FILE EXISTS, measured rather than imagined. On 2026-08-29, adding a custom tool whose
parameter was named ``class`` made EVERY custom tool disappear from ``tools/list``. The only
signal was::

    WARNING - Unexpected error during global custom tool registration;
              custom tools may not be available globally

which names neither the offending tool, nor the parameter, nor the cause. The useful traceback
(``ValueError: 'class' is not a valid parameter name``) was ten lines further down, below the fold.

Two defects, and the first is a guard that believes it already covers the case:

1. ``_build_signature`` / ``_build_annotations`` already skip unusable parameter names with
   ``if not param.name.isidentifier()``. But ``"class".isidentifier()`` is **True** -- it IS a
   lexically valid identifier -- while ``inspect.Parameter("class", ...)`` refuses it, because it
   is a RESERVED KEYWORD. The guard therefore lets through exactly what it exists to stop.
2. ``register_global_tools`` called ``_register_global_tool`` bare inside a loop while the caller
   caught the exception OUTSIDE that loop, so one bad tool took every following tool with it.

WHERE EACH GUARD ACTUALLY BITES -- and this was found by this file failing, not by reading:
``_register_global_tool`` ALREADY wraps ``self._mcp.tool(...)`` in its own try/except. A tool whose
REGISTRATION explodes was therefore already survivable upstream. What was NOT survivable is an
exception raised EARLIER in that same function -- in ``_build_global_tool_handler`` ->
``_build_signature`` -> ``inspect.Parameter`` -- which is precisely the ``class`` case, and which
escaped to the caller. The two tests below are split along that line on purpose.

HOW TO PROVE THESE ASSERTIONS CAN REDDEN
  PLAYED 2026-08-29, both mutations, each announced BEFORE running and matched name for name.
  Announcing WHICH cases must redden -- never how many -- is what makes these results immune to a
  noisy neighbour: an interference degrades at random, it does not reproduce two disjoint sets.
  (1) ``_is_usable_parameter_name`` reverted to a bare ``name.isidentifier()``
        -> 4 failed, 6 passed. RED: [class], [from], [lambda], and
           ``test_a_keyword_named_parameter_no_longer_breaks_its_own_tool`` (the tool vanishes
           entirely, which is the production symptom).
           GREEN: [name], [uss_class], [2bad], [with space] -- so the failure names the gap
           instead of blaming the whole guard.
  (2) ``register_global_tools`` reverted to calling ``_register_global_tool`` bare
        -> 1 failed, 9 passed. RED: ``test_a_handler_build_failure_does_not_sink_the_tools_after_it``
           and it ALONE, with ``ValueError: 'class' is not a valid parameter name`` escaping the
           loop -- the exact production trace.
  Restoring the sane file returns 10 passed both times.
  Both isolation tests assert on WHICH tools survived, never on how many -- a count cannot tell a
  working isolation from a loop that stopped one tool later.
"""
import pytest

from models.models import ToolDefinitionModel, ToolParameterModel
from services.custom_tool_service import CustomToolService, _is_usable_parameter_name


class _RecordingMcp:
    """A FastMCP stand-in that records registrations, and can be told to explode on one name."""

    def __init__(self, explode_on: str | None = None):
        self.registered: list[str] = []
        self._explode_on = explode_on

    def custom_route(self, _path, methods=None):  # noqa: ARG002 -- signature parity with FastMCP
        def _decorator(fn):
            return fn

        return _decorator

    def tool(self, name=None, description=None):  # noqa: ARG002 -- description is unused here
        def _decorator(fn):
            if name == self._explode_on:
                raise RuntimeError(f"boom on {name}")
            self.registered.append(name)
            return fn

        return _decorator


def _definition(name: str, parameter: str | None = None) -> ToolDefinitionModel:
    params = [ToolParameterModel(name=parameter, required=False)] if parameter else []
    return ToolDefinitionModel(name=name, description=f"{name} tool", parameters=params)


@pytest.mark.parametrize(
    ("candidate", "usable"),
    [
        ("name", True),
        ("uss_class", True),
        # The whole point: these are valid IDENTIFIERS and invalid PARAMETER names.
        ("class", False),
        ("from", False),
        ("lambda", False),
        # Still caught by the original identifier check -- kept so a rewrite cannot drop it.
        ("2bad", False),
        ("with space", False),
    ],
)
def test_python_keyword_is_not_a_usable_parameter_name(candidate: str, usable: bool):
    assert _is_usable_parameter_name(candidate) is usable


def test_a_keyword_named_parameter_no_longer_breaks_its_own_tool(monkeypatch):
    """The tool still registers; only the offending parameter drops out of the signature."""
    mcp = _RecordingMcp()
    service = CustomToolService(mcp)
    monkeypatch.setattr(service, "_get_builtin_tool_names", set)

    service.register_global_tools([_definition("keyword_param", parameter="class")])

    assert mcp.registered == ["keyword_param"]


def test_a_handler_build_failure_does_not_sink_the_tools_after_it(monkeypatch, caplog):
    """The gap PONT-07 actually closes: a throw BEFORE the pre-existing try/except.

    ``_register_global_tool`` already guards ``self._mcp.tool(...)``. It does NOT guard the handler
    construction above it, which is where the ``class`` parameter blew up -- so that is what is
    made to throw here.

    ``before`` and ``after`` surround the failing tool on purpose: asserting only on ``after``
    would still pass if the loop had silently skipped everything and registered nothing at all.
    """
    mcp = _RecordingMcp()
    service = CustomToolService(mcp)
    monkeypatch.setattr(service, "_get_builtin_tool_names", set)

    original = service._register_global_tool

    def _explode_on_boom(definition):
        if definition.name == "boom":
            raise ValueError("'class' is not a valid parameter name")
        return original(definition)

    monkeypatch.setattr(service, "_register_global_tool", _explode_on_boom)

    with caplog.at_level("WARNING"):
        service.register_global_tools(
            [_definition("before"), _definition("boom"), _definition("after")]
        )

    assert mcp.registered == ["before", "after"]
    assert "boom" in caplog.text
    assert "the remaining tools are unaffected" in caplog.text


def test_a_registration_failure_is_survived_by_the_upstream_guard(monkeypatch, caplog):
    """Characterises the guard that was ALREADY there, so a refactor cannot drop it silently.

    This one would pass without PONT-07 -- and saying so is the point: a test whose green does not
    depend on the change it sits next to must declare which guard it actually holds.
    """
    mcp = _RecordingMcp(explode_on="boom")
    service = CustomToolService(mcp)
    monkeypatch.setattr(service, "_get_builtin_tool_names", set)

    with caplog.at_level("WARNING"):
        service.register_global_tools(
            [_definition("before"), _definition("boom"), _definition("after")]
        )

    assert mcp.registered == ["before", "after"]
    assert "boom" in caplog.text
