from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.utils import coerce_bool, parse_json_payload, normalize_vector3, normalize_string_list
from services.tools.preflight import preflight


def _normalize_component_properties(value: Any) -> tuple[dict[str, dict[str, Any]] | None, str | None]:
    """
    Robustly normalize component_properties to a dict.
    Returns (parsed_dict, error_message). If error_message is set, parsed_dict is None.
    """
    if value is None:
        return None, None

    # Already a dict - validate structure
    if isinstance(value, dict):
        return value, None

    # Try parsing as JSON string
    if isinstance(value, str):
        # Check for obviously invalid values
        if value in ("[object Object]", "undefined", "null", ""):
            return None, f"component_properties received invalid value: '{value}'. Expected a JSON object like {{\"ComponentName\": {{\"property\": value}}}}"

        parsed = parse_json_payload(value)
        if isinstance(parsed, dict):
            return parsed, None

        return None, f"component_properties must be a JSON object (dict), got string that parsed to {type(parsed).__name__}"

    return None, f"component_properties must be a dict or JSON string, got {type(value).__name__}"


@mcp_for_unity_tool(
    description=(
        "Performs CRUD operations on GameObjects. "
        "Actions: create, modify, delete, duplicate, move_relative, look_at. "
        "NOT for searching — use the find_gameobjects tool to search by name/tag/layer/component/path. "
        "NOT for component management — use the manage_components tool (add/remove/set_property) "
        "or mcpforunity://scene/gameobject/{id}/components resource (read)."
    ),
    annotations=ToolAnnotations(
        title="Manage GameObject",
        destructiveHint=True,
    ),
)
async def manage_gameobject(
    ctx: Context,
    action: Annotated[Literal["create", "modify", "delete", "duplicate",
                              "move_relative", "look_at"] | None, "Action to perform on GameObject."] = None,
    target: Annotated[str | None,
                      "GameObject identifier by name, path, or instance ID for modify/delete/duplicate actions"] = None,
    search_method: Annotated[
        Literal["by_id", "by_name", "by_path", "by_tag", "by_layer", "by_component"] | None,
        "How to resolve 'target'. If omitted, Unity infers: instance ID -> by_id, "
        "path (contains '/') -> by_path, otherwise by_name."
    ] = None,
    name: Annotated[str | None,
                    "GameObject name for 'create' (initial name) and 'modify' (rename) actions."] = None,
    tag: Annotated[str | None,
                   "Tag name - used for both 'create' (initial tag) and 'modify' (change tag)"] = None,
    parent: Annotated[str | None,
                      "Parent GameObject reference - used for both 'create' (initial parent) and 'modify' (change parent)"] = None,
    position: Annotated[list[float] | dict[str, float] | str | None,
                        "Position as [x, y, z] array, {x, y, z} object, or JSON string"] = None,
    rotation: Annotated[list[float] | dict[str, float] | str | None,
                        "Rotation as [x, y, z] euler angles array, {x, y, z} object, or JSON string"] = None,
    scale: Annotated[list[float] | dict[str, float] | str | None,
                     "Scale as [x, y, z] array, {x, y, z} object, or JSON string"] = None,
    components_to_add: Annotated[list[str] | str | None,
                                 "List of component names to add during 'create' or 'modify'"] = None,
    primitive_type: Annotated[str | None,
                              "Primitive type for 'create' action"] = None,
    save_as_prefab: Annotated[bool | str | None,
                              "If True, saves the created GameObject as a prefab (accepts true/false or 'true'/'false')"] = None,
    prefab_path: Annotated[str | None, "Path for prefab creation"] = None,
    prefab_folder: Annotated[str | None,
                             "Folder for prefab creation"] = None,
    # --- Parameters for 'modify' ---
    set_active: Annotated[bool | str | None,
                          "If True, sets the GameObject active (accepts true/false or 'true'/'false')"] = None,
    layer: Annotated[str | None, "Layer name"] = None,
    is_static: Annotated[bool | str | None,
                         "Set the GameObject's static flag. true = all StaticEditorFlags, false = none (accepts true/false or 'true'/'false')"] = None,
    components_to_remove: Annotated[list[str] | str | None,
                                    "List of component names to remove"] = None,
    component_properties: Annotated[dict[str, dict[str, Any]] | str | None,
                                    """Dictionary of component names to their properties to set. For example:
                                    `{"MyScript": {"otherObject": {"find": "Player", "method": "by_name"}}}` assigns GameObject
                                    `{"MyScript": {"playerHealth": {"find": "Player", "component": "HealthComponent"}}}` assigns Component
                                    Example set nested property:
                                    - Access shared material: `{"MeshRenderer": {"sharedMaterial.color": [1, 0, 0, 1]}}`"""] = None,
    # --- Parameters for 'duplicate' ---
    new_name: Annotated[str | None,
                        "New name for the duplicated object (default: SourceName_Copy)"] = None,
    offset: Annotated[list[float] | str | None,
                      "Offset from original/reference position as [x, y, z] array (list or JSON string)"] = None,
    # --- Parameters for 'move_relative' ---
    reference_object: Annotated[str | None,
                                "Reference object for relative movement (required for move_relative)"] = None,
    direction: Annotated[Literal["left", "right", "up", "down", "forward", "back", "front", "backward", "behind"] | None,
                         "Direction for relative movement (e.g., 'right', 'up', 'forward')"] = None,
    distance: Annotated[float | None,
                        "Distance to move in the specified direction (default: 1.0)"] = None,
    world_space: Annotated[bool | str | None,
                           "If True (default), use world space directions; if False, use reference object's local directions"] = None,
    # --- Parameters for 'look_at' ---
    look_at_target: Annotated[list[float] | str | None,
                              "World position [x,y,z] or GameObject name/path/ID to look at (for look_at action)."] = None,
    look_at_up: Annotated[list[float] | str | None,
                          "Optional up vector [x,y,z] for look_at. Defaults to [0,1,0]."] = None,
) -> dict[str, Any]:
    # Get active instance from session state
    # Removed session_state import
    unity_instance = await get_unity_instance_from_context(ctx)

    gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=True)
    if gate is not None:
        return gate.model_dump()

    if action is None:
        return {
            "success": False,
            "message": "Missing required parameter 'action'. Valid actions: create, modify, delete, duplicate, move_relative, look_at. To SEARCH for GameObjects use the find_gameobjects tool. To manage COMPONENTS use the manage_components tool."
        }

    # --- Normalize vector parameters with detailed error handling ---
    position, position_error = normalize_vector3(position, "position")
    if position_error:
        return {"success": False, "message": position_error}
    rotation, rotation_error = normalize_vector3(rotation, "rotation")
    if rotation_error:
        return {"success": False, "message": rotation_error}
    scale, scale_error = normalize_vector3(scale, "scale")
    if scale_error:
        return {"success": False, "message": scale_error}
    offset, offset_error = normalize_vector3(offset, "offset")
    if offset_error:
        return {"success": False, "message": offset_error}

    # --- Normalize boolean parameters ---
    save_as_prefab = coerce_bool(save_as_prefab)
    set_active = coerce_bool(set_active)
    is_static = coerce_bool(is_static)
    world_space = coerce_bool(world_space, default=True)

    # --- Normalize component_properties with detailed error handling ---
    component_properties, comp_props_error = _normalize_component_properties(
        component_properties)
    if comp_props_error:
        return {"success": False, "message": comp_props_error}

    # --- Normalize components_to_add and components_to_remove ---
    components_to_add, add_error = normalize_string_list(components_to_add, "components_to_add")
    if add_error:
        return {"success": False, "message": add_error}

    components_to_remove, remove_error = normalize_string_list(components_to_remove, "components_to_remove")
    if remove_error:
        return {"success": False, "message": remove_error}

    try:
        # Prepare parameters, removing None values
        params = {
            "action": action,
            "target": target,
            "searchMethod": search_method,
            "name": name,
            "tag": tag,
            "parent": parent,
            "position": position,
            "rotation": rotation,
            "scale": scale,
            "componentsToAdd": components_to_add,
            "primitiveType": primitive_type,
            "saveAsPrefab": save_as_prefab,
            "prefabPath": prefab_path,
            "prefabFolder": prefab_folder,
            "setActive": set_active,
            "layer": layer,
            "isStatic": is_static,
            "componentsToRemove": components_to_remove,
            "componentProperties": component_properties,
            # Parameters for 'duplicate'
            "new_name": new_name,
            "offset": offset,
            # Parameters for 'move_relative'
            "reference_object": reference_object,
            "direction": direction,
            "distance": distance,
            "world_space": world_space,
            # Parameters for 'look_at'
            "look_at_target": look_at_target,
            "look_at_up": look_at_up,
        }
        params = {k: v for k, v in params.items() if v is not None}

        # --- Handle Prefab Path Logic ---
        # Check if 'saveAsPrefab' is explicitly True in params
        if action == "create" and params.get("saveAsPrefab"):
            if "prefabPath" not in params:
                if "name" not in params or not params["name"]:
                    return {"success": False, "message": "Cannot create default prefab path: 'name' parameter is missing."}
                # Use the provided prefab_folder (which has a default) and the name to construct the path
                constructed_path = f"{prefab_folder}/{params['name']}.prefab"
                # Ensure clean path separators (Unity prefers '/')
                params["prefabPath"] = constructed_path.replace("\\", "/")
            elif not params["prefabPath"].lower().endswith(".prefab"):
                return {"success": False, "message": f"Invalid prefab_path: '{params['prefabPath']}' must end with .prefab"}
        # Ensure prefabFolder itself isn't sent if prefabPath was constructed or provided
        # The C# side only needs the final prefabPath
        params.pop("prefabFolder", None)
        # --------------------------------

        # Use centralized retry helper with instance routing
        response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "manage_gameobject",
            params,
        )

        # Check if the response indicates success
        # If the response is not successful, raise an exception with the error message
        if isinstance(response, dict) and response.get("success"):
            return {"success": True, "message": response.get("message", "GameObject operation successful."), "data": response.get("data")}
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}

    except Exception as e:
        return {"success": False, "message": f"Python error managing GameObject: {e!s}"}
