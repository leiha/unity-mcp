"""PONT-08 -- a custom tool whose schema changed in Unity must reach the LLM without a restart.

WHY THIS FILE EXISTS, measured rather than imagined. On 2026-08-29 a custom tool's description was
changed in Unity, the editor was made to reload, and the new text was verified BY REFLECTION to be
live inside the editor -- yet every MCP client kept reading the old schema until
``systemctl --user restart mcp-for-unity``. Nothing said so. An LLM chooses its arguments from that
schema, so the freeze does not merely hide an update: it makes the server describe a tool that no
longer exists that way. That is the one failure this project refuses -- a system lying about its
own state.

THE FREEZE HAS TWO LAYERS, and a judge that only sees one of them is worthless:

1. ``CustomToolService._global_tools`` -- ``_register_global_tool`` used to ``return`` on any name
   it already knew, keeping the first definition for the whole life of the process.
2. ``FastMCP``'s own ``ToolManager._tools`` -- and this one is the reason a "just call
   ``mcp.tool()`` again" fix does NOT work::

       existing = self._tools.get(tool.name)
       if existing:
           ...
           return existing          # <- add_tool never replaces
                                    #    (mcp/server/fastmcp/tools/tool_manager.py:67-71)

   ``remove_tool`` first is therefore required, not defensive.

⛔ THESE TESTS USE A REAL ``FastMCP`` AND READ ``await mcp.list_tools()`` ON PURPOSE. A recording
double (like the one in ``test_custom_tool_service_registration_isolation.py``) would only ever
show layer 1, and would go green on a fix that leaves the LLM reading a stale schema -- the
"instrument out of tune with its subject" failure mode. The subject here is ``tools/list``, so the
judge reads ``tools/list``.

HOW TO PROVE THESE ASSERTIONS CAN REDDEN
  PLAYED 2026-08-29, four mutations, each announced by NAME before running -- never by a count. A
  count cannot tell a real failure from a noisy neighbour on a machine nine sessions share, whereas
  four disjoint named sets cannot all be reproduced by interference.

  MUT-A  `self._mcp.remove_tool(...)` -> `pass`   (our dict updates, FastMCP's does not)
         RED: ..._changed_description_reaches_the_served_schema
              ..._changed_parameter_set_reaches_the_served_schema
         GREEN: ..._unchanged_definition_is_not_re_registered
                ..._cannot_be_built_leaves_the_working_tool_served
         -> this is the mutation that proves layer 2 is real: without `remove_tool` the fix looks
            done and the LLM still reads the stale schema.

  MUT-B  the `existing.model_dump() == definition.model_dump()` short-circuit removed
         RED: ..._unchanged_definition_is_not_re_registered, AND IT ALONE
         -> the tool is torn down and rebuilt on every one of Unity's reconnections.

  MUT-C  the handler build moved AFTER the removal (remove-then-build)
         RED: ..._cannot_be_built_leaves_the_working_tool_served, AND IT ALONE
         -> the incumbent is deleted and nothing replaces it: the tool disappears entirely.

  MUT-D  `tests/integration/conftest.py` returned to its unconditional `mcp` stub
         RED: the WHOLE SUITE, at collection -- `1 error`, 0 tests run.
         -> that repair ships with this file for a reason; see the fixture docstring.

  The sane tree: 4 passed alone, and 1276 passed / 3 skipped for the whole suite (baseline before
  this file: 1272 passed / 3 skipped).
"""
import asyncio

import pytest
from mcp.server.fastmcp import FastMCP

from models.models import ToolDefinitionModel, ToolParameterModel
from services.custom_tool_service import CustomToolService


def _definition(description: str, *parameters: str) -> ToolDefinitionModel:
    return ToolDefinitionModel(
        name="pont08_tool",
        description=description,
        parameters=[ToolParameterModel(name=p, required=False) for p in parameters],
    )


@pytest.fixture
def service(monkeypatch):
    """A service on a REAL FastMCP, and a decor PROVEN to be standing before any test uses it.

    Two pieces of process-global state have to be pinned, and the second one is a trap this file
    paid for:

    * ``_get_builtin_tool_names`` reads a registry other modules populate. Pinned to the empty set
      so this file does not depend on collection order.
    * ``custom_tool_service.Context`` -- ``tests/integration/conftest.py`` replaces it with a
      ``_DummyContext`` at COLLECTION time, inside the production module itself. Every handler
      built afterwards is annotated with that double, and pydantic then refuses to derive a schema
      from it: ``Cannot generate a JsonSchema for core_schema.IsInstanceSchema (_DummyContext)``.
      Run alone, this file passed; run after the integration conftest, all of it failed -- and the
      warning naming the real cause was swallowed by ``_register_global_tool``'s own try/except.
      Restoring the REAL ``Context`` is not a workaround: the subject under test is the schema an
      MCP client receives, so the decor must be built with the same type production uses.

    The canary at the end is the point. Without it a poisoned decor reports itself as "the schema
    did not refresh" -- blaming the law instead of the decor.
    """
    from mcp.server.fastmcp import Context as RealContext

    import services.custom_tool_service as cts

    monkeypatch.setattr(cts, "Context", RealContext)

    mcp = FastMCP("pont08")
    svc = CustomToolService(mcp)
    monkeypatch.setattr(svc, "_get_builtin_tool_names", set)

    canary = ToolDefinitionModel(name="pont08_canary", description="canary", parameters=[])
    svc.register_global_tools([canary])
    assert any(t.name == "pont08_canary" for t in asyncio.run(mcp.list_tools())), (
        "the decor cannot register a trivial tool at all -- something poisoned this process "
        "before the fixture ran; do not read the failures below as a schema-refresh defect"
    )

    return svc, mcp


def _served(mcp) -> dict:
    """What an MCP client actually receives for our tool -- description and argument names."""
    tools = asyncio.run(mcp.list_tools())
    mine = [t for t in tools if t.name == "pont08_tool"]
    assert mine, (
        "'pont08_tool' is not served at all. TWO causes look identical here and the message must "
        "not pick one: either the decor never got built (see the fixture's canary -- it would have "
        "fired first), or the code under test removed the incumbent and failed to put a "
        "replacement back, which is the regression `..._leaves_the_working_tool_served` exists for."
    )
    tool = mine[0]
    return {
        "description": tool.description,
        "arguments": sorted(k for k in tool.inputSchema.get("properties", {}) if k != "ctx"),
    }


def test_a_changed_description_reaches_the_served_schema(service):
    svc, mcp = service
    svc.register_global_tools([_definition("VERSION-A")])
    assert _served(mcp)["description"] == "VERSION-A"  # decor asserted before it is used

    svc.register_global_tools([_definition("VERSION-B")])

    assert _served(mcp)["description"] == "VERSION-B"


def test_a_changed_parameter_set_reaches_the_served_schema(service):
    """The one that actually costs an LLM: it picks its arguments from this list.

    Asserted as a SHIFT -- ``beta`` in AND ``alpha`` out -- because a fix that only ever appends
    would satisfy the first half alone.
    """
    svc, mcp = service
    svc.register_global_tools([_definition("desc", "alpha")])
    assert _served(mcp)["arguments"] == ["alpha"]

    svc.register_global_tools([_definition("desc", "beta")])

    assert _served(mcp)["arguments"] == ["beta"]


def test_an_unchanged_definition_is_not_re_registered(service):
    """The other edge, and it is not decoration.

    Unity re-sends its whole tool list on every reconnection -- eight times in twenty minutes on
    2026-08-29. If the fix replaced unconditionally, every reconnection would tear down and rebuild
    every custom tool. Identity of the served object is what proves it did not.
    """
    svc, mcp = service
    svc.register_global_tools([_definition("stable", "alpha")])
    before = mcp._tool_manager.get_tool("pont08_tool")

    svc.register_global_tools([_definition("stable", "alpha")])

    assert mcp._tool_manager.get_tool("pont08_tool") is before


def test_a_new_definition_that_cannot_be_built_leaves_the_working_tool_served(service, monkeypatch):
    """ORDER, not politeness: build the replacement BEFORE removing the incumbent.

    Remove-then-build would delete a working tool and fail to put anything back -- the workshop
    would lose the tool entirely because someone shipped a bad definition, which is strictly worse
    than the freeze this file exists to remove.
    """
    svc, mcp = service
    svc.register_global_tools([_definition("VERSION-A", "alpha")])

    def _explode(_definition_arg):
        raise ValueError("this definition cannot become a signature")

    monkeypatch.setattr(svc, "_build_signature", _explode)
    svc.register_global_tools([_definition("VERSION-B", "beta")])

    assert _served(mcp) == {"description": "VERSION-A", "arguments": ["alpha"]}
