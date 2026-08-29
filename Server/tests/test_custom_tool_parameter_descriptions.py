"""PONT-09 -- a custom tool's PARAMETER descriptions must reach the schema an LLM reads.

WHY THIS FILE EXISTS, measured rather than imagined. On 2026-08-29, `tools/list` was asked what it
serves for every parameter of every tool::

    execute_code    6 parameters, 4 with a description     <- a BUILT-IN tool
    manage_scene   19 parameters, 1 with a description     <- a BUILT-IN tool
    ui_find         6 parameters, 0 with a description     <- ours
    di_state        3 parameters, 0 with a description     <- ours
    trace           3 parameters, 0 with a description     <- ours

Every custom tool loses ALL of them, and the built-ins keep theirs -- so the failure is in the
custom-tool path alone. The Unity side is not at fault: `ToolDiscoveryService.cs` copies
`paramAttr.Description` into the payload, and `ToolParameterModel` has a `description` field that
arrives populated. It is thrown away here, in `_build_signature` and `_build_annotations`, which
annotate each parameter with its bare TYPE and nothing else.

⭐ WHY IT MATTERS MORE THAN IT LOOKS. This project's owner set one criterion for this fork: an
instrument "as perfect as possible for an LLM to use". An LLM chooses its arguments from this
schema and from nothing else. `tag: string` with no sentence attached is a parameter it has to
guess at -- while the sentence explaining it was written, in C#, with care, and travelled all the
way across the bridge before being dropped one function short of its reader. We were writing
documentation for nobody.

HOW TO PROVE THESE ASSERTIONS CAN REDDEN
  PLAYED 2026-08-29, four mutations, each announced by NAME before running -- never by a count. A
  count cannot tell a real failure from a noisy neighbour on a machine nine sessions share; four
  disjoint named sets cannot all be reproduced by interference.

  MUT-A  `_annotate` returns the bare type again (the defect, restored)
         RED: ..._reaches_the_served_schema · ..._are_not_confused · ..._keep_their_own_descriptions
         GREEN: ..._gains_no_invented_one · ..._default_...survives_the_description

  MUT-C  the optional parameter loses its default (`default=inspect._empty`)
         RED: ..._default_of_an_optional_parameter_survives_the_description, AND IT ALONE
         -> proves the repair is not allowed to cost a parameter its optionality.

  MUT-D  a description is FABRICATED when Unity sent none (`param.description or "TODO"`)
         RED: ..._gains_no_invented_one, AND IT ALONE
         -> this is what that edge actually guards, and it took MUT-B to find out.

  MUT-B  the `if not param.description: return mapped` short-circuit removed
         RED: NOTHING. All five stayed green.
         ⭐⭐ AND THIS IS THE USEFUL RESULT OF THE FOUR. An inert mutation is not a failed
         calibration, it is the symptom of a law written twice -- and it was: pydantic already
         treats `Field(description=None)` as no description, so our short-circuit decided nothing.
         The half that OWNS the schema is pydantic's, so ours was deleted rather than kept as a
         comfortable-looking guard that guards nothing. MUT-D was then written to find the mutation
         that DOES redden that edge, and it does.
         ⇒ the judge that was almost decorative is now load-bearing, and the production code is one
           branch shorter. Neither would have happened without playing a mutation that did nothing.

  The sane tree: 5 passed alone, 1281 passed / 3 skipped for the whole suite.
"""
import asyncio

import pytest
from mcp.server.fastmcp import FastMCP

from models.models import ToolDefinitionModel, ToolParameterModel
from services.custom_tool_service import CustomToolService


@pytest.fixture
def service(monkeypatch):
    """Same two pins as `test_custom_tool_schema_refresh.py`, and for the same reasons.

    `custom_tool_service.Context` is replaced by a double at COLLECTION time by
    `tests/integration/conftest.py`; a handler annotated with that double cannot be given a schema
    by pydantic, and the resulting failure blames the law instead of the decor. The canary makes a
    poisoned decor say so itself.
    """
    from mcp.server.fastmcp import Context as RealContext

    import services.custom_tool_service as cts

    monkeypatch.setattr(cts, "Context", RealContext)

    mcp = FastMCP("pont09")
    svc = CustomToolService(mcp)
    monkeypatch.setattr(svc, "_get_builtin_tool_names", set)

    canary = ToolDefinitionModel(name="pont09_canary", description="canary", parameters=[])
    svc.register_global_tools([canary])
    assert any(t.name == "pont09_canary" for t in asyncio.run(mcp.list_tools())), (
        "the decor cannot register a trivial tool at all -- something poisoned this process "
        "before the fixture ran; do not read the failures below as a description defect"
    )
    return svc, mcp


def _properties(mcp, tool_name: str) -> dict:
    tools = asyncio.run(mcp.list_tools())
    mine = [t for t in tools if t.name == tool_name]
    assert mine, (
        f"'{tool_name}' is not served at all. Either the decor never got built (the fixture's "
        "canary would have fired first), or registration failed -- do not read this as a "
        "description defect."
    )
    return mine[0].inputSchema.get("properties", {})


def test_a_parameter_description_reaches_the_served_schema(service):
    """The whole point: the sentence written in C# is what an LLM reads, or nobody does."""
    svc, mcp = service
    svc.register_global_tools([ToolDefinitionModel(
        name="pont09_tool",
        description="tool level description",
        parameters=[ToolParameterModel(
            name="tag",
            description="Your marker, e.g. SONDE-PONT-6641bdf4. Brackets are added if missing.",
            required=True,
        )],
    )])

    props = _properties(mcp, "pont09_tool")
    assert "tag" in props, "the parameter itself is missing; that is a different defect"
    assert props["tag"].get("description") == (
        "Your marker, e.g. SONDE-PONT-6641bdf4. Brackets are added if missing."
    )


def test_the_tool_description_and_the_parameter_description_are_not_confused(service):
    """Asserted as a DIFFERENCE, because a fix that pasted the tool's own description onto every
    parameter would satisfy the test above and be worse than the defect."""
    svc, mcp = service
    svc.register_global_tools([ToolDefinitionModel(
        name="pont09_two",
        description="TOOL LEVEL",
        parameters=[ToolParameterModel(name="alpha", description="ALPHA LEVEL", required=False)],
    )])

    props = _properties(mcp, "pont09_two")
    assert props["alpha"].get("description") == "ALPHA LEVEL"


def test_two_parameters_keep_their_own_descriptions(service):
    """The SHIFT form: two parameters, two different sentences, asserted to DIFFER.

    A single parameter can always be made green by pasting any string in. Two that must disagree
    cannot be satisfied by one shared value.
    """
    svc, mcp = service
    svc.register_global_tools([ToolDefinitionModel(
        name="pont09_three",
        description="tool",
        parameters=[
            ToolParameterModel(name="alpha", description="FIRST SENTENCE", required=False),
            ToolParameterModel(name="beta", description="SECOND SENTENCE", required=False),
        ],
    )])

    props = _properties(mcp, "pont09_three")
    assert props["alpha"].get("description") == "FIRST SENTENCE"
    assert props["beta"].get("description") == "SECOND SENTENCE"
    assert props["alpha"].get("description") != props["beta"].get("description")


def test_a_parameter_without_a_description_gains_no_invented_one(service):
    """The other edge. Unity may send a parameter with no sentence; inventing one there would put
    words in the tool author's mouth, which is worse than silence."""
    svc, mcp = service
    svc.register_global_tools([ToolDefinitionModel(
        name="pont09_bare",
        description="tool",
        parameters=[ToolParameterModel(name="alpha", required=False)],
    )])

    props = _properties(mcp, "pont09_bare")
    assert "alpha" in props
    assert not props["alpha"].get("description")


def test_the_default_of_an_optional_parameter_survives_the_description(service):
    """Guards the repair itself: annotating a parameter must not cost it its default, or every
    optional parameter silently becomes required."""
    svc, mcp = service
    svc.register_global_tools([ToolDefinitionModel(
        name="pont09_default",
        description="tool",
        parameters=[
            ToolParameterModel(name="needed", description="required one", required=True),
            ToolParameterModel(name="spare", description="optional one", required=False),
        ],
    )])

    tools = asyncio.run(mcp.list_tools())
    schema = [t for t in tools if t.name == "pont09_default"][0].inputSchema
    assert "needed" in schema.get("required", [])
    assert "spare" not in schema.get("required", [])
