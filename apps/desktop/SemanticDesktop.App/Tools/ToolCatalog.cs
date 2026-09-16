using System.Text.Json;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Providers;

namespace SemanticDesktop.App.Tools;

public static class ToolCatalog
{
    public const string BuiltinMcpId = McpIds.Builtin;
    public const string BuiltinDisplayName = McpIds.BuiltinDisplayName;

    private static readonly JsonElement ObjectSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    public static IReadOnlyList<AgentToolDefinition> ComputerControlTools { get; } = CreateTools();

    public static IReadOnlySet<string> ComputerControlToolNames { get; } =
        ComputerControlTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    public static bool IsComputerControl(string toolName) =>
        ComputerControlToolNames.Contains(toolName);

    private static IReadOnlyList<AgentToolDefinition> CreateTools()
    {
        (string Name, string Description)[] items =
        [
            ("desktop.get_state", "Return a concise snapshot of desktop windows and agent session state."),
            ("desktop.get_capabilities", "Return agent capability flags and supported tool names."),
            ("desktop.describe", "Return a compact live semantic desktop graph (apps, windows, tabs, important controls, relevant state) as text plus structured graph."),
            ("desktop.get_graph", "Return the structured semantic desktop graph without the compact text rendering."),
            ("desktop.batch", "Execute multiple agent commands in one round trip, fusing filesystem stats and running safe reads in parallel."),
            ("desktop.diff", "Return a compact state-change diff of the live desktop graph versus the previous snapshot."),
            ("monitor.list", "List connected monitors with bounds, work area, primary flag, and DPI scale."),
            ("window.list", "List top-level windows."),
            ("window.get", "Get a single window by id with bounds, restore bounds, monitor index, and show state."),
            ("window.focus", "Focus a window by handle id."),
            ("window.minimize", "Minimize a window by id."),
            ("window.maximize", "Maximize a window by id."),
            ("window.restore", "Restore a minimized or maximized window by id."),
            ("window.move", "Move a window using virtual coordinates and/or monitor index; optional placement maximize."),
            ("window.resize", "Resize a window to explicit width/height in physical pixels; optional x/y with omitted axes preserving current position."),
            ("window.wait_for", "Wait until a window matching process/title/titleRegex appears."),
            ("ui.get_tree", "Get a bounded UI Automation subtree for a window or root element."),
            ("ui.find", "Find UI elements matching a semantic selector."),
            ("ui.invoke", "Invoke the default action on a UI element."),
            ("ui.set_value", "Set the value of an editable UI element."),
            ("ui.get_text", "Read text from a UI element (sensitive values are redacted)."),
            ("ui.wait_for", "Wait until a UI element matching the selector exists."),
            ("process.list", "List running processes."),
            ("process.launch", "Launch an executable process."),
            ("filesystem.list", "List files and directories at a path."),
            ("filesystem.exists", "Check whether a filesystem path exists."),
            ("filesystem.read_text", "Read a text file."),
            ("filesystem.write_text", "Write a text file."),
            ("filesystem.stat", "Return size/mtime/exists metadata for a filesystem path."),
            ("filesystem.inspect", "List a directory with per-entry stats and optional extra path stats in one call."),
            ("plan.execute", "Execute a multi-step automation plan."),
            ("plan.cancel", "Cancel a running plan by id."),
            ("system.emergency_stop", "Engage the agent emergency stop gate."),
            ("system.performance", "Return local in-process timing aggregates (count, success/failure, p50/p95) by operation."),
            ("events.subscribe", "Subscribe to desktop state-change events (window/focus/mutation)."),
            ("events.poll", "Poll a prior events.subscribe cursor for new events."),
            ("events.unsubscribe", "Remove an event subscription."),
            ("browser.list", "List discovered browser instances with CDP endpoints."),
            ("browser.tabs", "List open tabs for a browser instance."),
            ("browser.get_tab", "Get details for a browser tab by id."),
            ("browser.open_tab", "Open a new browser tab, optionally navigating to a URL."),
            ("browser.close_tab", "Close a browser tab by id."),
            ("browser.navigate", "Navigate a tab to a URL."),
            ("browser.back", "Navigate the tab history backward."),
            ("browser.forward", "Navigate the tab history forward."),
            ("browser.reload", "Reload the current tab page."),
            ("browser.query", "Query the first element matching a browser selector."),
            ("browser.query_all", "Query all elements matching a browser selector."),
            ("browser.click", "Click an element by id or selector."),
            ("browser.fill", "Fill an input element with a value."),
            ("browser.select", "Select an option in a select element."),
            ("browser.focus", "Focus an element by id or selector."),
            ("browser.get_text", "Read text content from an element."),
            ("browser.get_dom", "Get a bounded DOM snapshot for a tab."),
            ("browser.get_accessibility_tree", "Get a bounded accessibility tree for a tab."),
            ("browser.wait_for", "Wait until a selector matches an element."),
            ("browser.wait_for_navigation", "Wait until navigation completes."),
            ("browser.wait_for_network_idle", "Wait until network activity is quiet."),
            ("browser.get_downloads", "List recent browser downloads tracked by the agent."),
            ("adapter.list", "List application adapters and their availability/actions."),
            ("adapter.capabilities", "Get capabilities for one adapter or all adapters."),
            ("adapter.execute", "Execute an application-adapter action (Blender/VS Code/Visual Studio/Roblox Studio)."),
            ("blender.open", "Open a .blend file. Default mode background validates headlessly; mode gui launches visible Blender."),
            ("blender.get_scene", "Inspect the Blender scene via Python scripting."),
            ("blender.get_objects", "List Blender objects via Python scripting."),
            ("blender.select_object", "Select a Blender object by name via Python."),
            ("blender.execute_python", "Run a Blender Python snippet."),
            ("blender.export", "Export the Blender scene (obj/fbx/gltf/stl) via Python."),
            ("blender.save", "Save the Blender file via Python."),
            ("blender.batch", "Run 1..32 allowlisted Blender operations in one background process."),
            ("blender.render", "Render still frame to an absolute output path (background or live bridge)."),
            ("blender.import_mesh", "Import obj/fbx/gltf/glb/stl mesh (background or live bridge)."),
            ("vscode.open_file", "Open a file in VS Code via the code CLI."),
            ("vscode.open_folder", "Open a folder/workspace in VS Code via the code CLI."),
            ("vscode.execute_command", "Attempt a VS Code CLI --command invocation."),
            ("vscode.get_workspace", "Return workspace path metadata for VS Code."),
            ("visualstudio.get_solution", "Parse Visual Studio solution projects from a .sln path."),
            ("visualstudio.build", "Build a solution via MSBuild or devenv."),
            ("visualstudio.open_file", "Open a file in Visual Studio (devenv /Edit)."),
            ("visualstudio.open_solution", "Open a .sln in Visual Studio (devenv)."),
            ("roblox.open_place", "Launch Roblox Studio with an absolute .rbxl/.rbxlx place path."),
            ("roblox.plugin_ping", "Report Roblox bridge listener and Studio plugin connection state."),
            ("roblox.get_hierarchy", "Get a bounded instance hierarchy from the connected Roblox Studio plugin."),
            ("roblox.get_selection", "Get selected instances from the connected Roblox Studio plugin."),
            ("roblox.select", "Select instances by session-scoped IDs via the Roblox Studio plugin."),
            ("roblox.set_property", "Set an allowlisted property on a Roblox instance via the Studio plugin."),
            ("input.mouse_move", "Fallback: move the mouse to physical screen coordinates (DPI/virtual-desktop aware)."),
            ("input.mouse_click", "Fallback: click at physical screen coordinates via SendInput."),
            ("input.mouse_drag", "Fallback: drag between physical screen coordinates via SendInput."),
            ("input.scroll", "Fallback: scroll vertically or horizontally via SendInput."),
            ("input.key", "Fallback: press/down/up a key via SendInput."),
            ("input.hotkey", "Fallback: chord hotkey via SendInput (keys array or '+'-joined)."),
            ("input.type", "Fallback: type unicode text via SendInput."),
            ("vision.capture_screen", "Capture the virtual desktop or a monitor as PNG (vision fallback; record visionReason)."),
            ("vision.capture_window", "Capture a window by id as PNG via PrintWindow/BitBlt (vision fallback)."),
            ("vision.capture_region", "Capture a physical screen region as PNG (vision fallback).")
        ];

        return items.Select(i => new AgentToolDefinition
        {
            Name = i.Name,
            Description = i.Description,
            JsonSchema = ObjectSchema,
            Source = BuiltinMcpId
        }).ToList();
    }
}
