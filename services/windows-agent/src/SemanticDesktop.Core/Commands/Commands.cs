namespace SemanticDesktop.Core.Commands;

public sealed class WindowFocusRequest
{
    public required string WindowId { get; init; }
}

public sealed class WindowIdRequest
{
    public required string WindowId { get; init; }
}

public sealed class WindowMoveRequest
{
    public required string WindowId { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Monitor { get; init; }
    public string Placement { get; init; } = "preserve";
}

public sealed class WindowResizeRequest
{
    public required string WindowId { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
}

public sealed class UIFindQuery
{
    public string? WindowId { get; init; }
    public string? RootId { get; init; }
    public UIFindSelector Selector { get; init; } = new();
    public int? Depth { get; init; }
    public int? MaxResults { get; init; }
}

public sealed class UIFindSelector
{
    public string? Name { get; init; }
    public string? NameContains { get; init; }
    public string? AutomationId { get; init; }
    public string? ClassName { get; init; }
    public string? ControlType { get; init; }
    public string? FrameworkId { get; init; }
    public bool? Enabled { get; init; }
    public bool? Offscreen { get; init; }
}

public sealed class UIFindResult
{
    public IReadOnlyList<Models.UIElementSummary> Elements { get; init; } = Array.Empty<Models.UIElementSummary>();
    public int MatchCount { get; init; }
    public double? Confidence { get; init; }
}

public sealed class UITreeQuery
{
    public string? RootId { get; init; }
    public string? WindowId { get; init; }
    public string TreeMode { get; init; } = "control";
    public int? Depth { get; init; }
    public int? MaxNodes { get; init; }
    public bool InteractiveOnly { get; init; }
    public bool IncludeText { get; init; }
    public bool IncludeBounds { get; init; } = true;
}

public sealed class ElementActionRequest
{
    public required string ElementId { get; init; }
}

public sealed class SetValueRequest
{
    public required string ElementId { get; init; }
    public required string Value { get; init; }
}

public sealed class ProcessLaunchRequest
{
    public required string Executable { get; init; }
    public string[]? Args { get; init; }
    public string? WorkingDirectory { get; init; }
}

public static class CommandNames
{
    public const string MonitorList = "monitor.list";
    public const string WindowList = "window.list";
    public const string WindowGet = "window.get";
    public const string WindowFocus = "window.focus";
    public const string WindowMinimize = "window.minimize";
    public const string WindowMaximize = "window.maximize";
    public const string WindowRestore = "window.restore";
    public const string WindowMove = "window.move";
    public const string WindowResize = "window.resize";
    public const string UiGetTree = "ui.get_tree";
    public const string UiFind = "ui.find";
    public const string UiInvoke = "ui.invoke";
    public const string UiSetValue = "ui.set_value";
    public const string UiGetText = "ui.get_text";
    public const string ProcessLaunch = "process.launch";
    public const string SystemPing = "system.ping";
    public const string PlanExecute = "plan.execute";
    public const string PlanGet = "plan.get";
    public const string PlanCancel = "plan.cancel";
    public const string FilesystemWriteText = "filesystem.write_text";
    public const string FilesystemExists = "filesystem.exists";
    public const string FilesystemList = "filesystem.list";
    public const string FilesystemReadText = "filesystem.read_text";
    public const string FilesystemStat = "filesystem.stat";
    public const string FilesystemInspect = "filesystem.inspect";
    public const string ProcessList = "process.list";
    public const string DesktopGetState = "desktop.get_state";
    public const string DesktopGetCapabilities = "desktop.get_capabilities";
    public const string DesktopDescribe = "desktop.describe";
    public const string DesktopGetGraph = "desktop.get_graph";
    public const string DesktopBatch = "desktop.batch";
    public const string DesktopDiff = "desktop.diff";
    public const string EventsSubscribe = "events.subscribe";
    public const string EventsPoll = "events.poll";
    public const string EventsUnsubscribe = "events.unsubscribe";
    public const string WindowWaitFor = "window.wait_for";
    public const string UiWaitFor = "ui.wait_for";
    public const string SessionCreate = "session.create";
    public const string SessionGet = "session.get";
    public const string SessionList = "session.list";
    public const string PermissionApprove = "permission.approve";
    public const string PermissionDeny = "permission.deny";
    public const string PermissionPending = "permission.pending";
    public const string PermissionPolicyGet = "permission.policy.get";
    public const string PermissionPolicySet = "permission.policy.set";
    public const string AuditList = "audit.list";
    public const string SystemStatus = "system.status";
    public const string SystemPerformance = "system.performance";
    public const string SystemEmergencyStop = "system.emergency_stop";
    public const string SystemEmergencyStopClear = "system.emergency_stop.clear";

    public const string BrowserList = "browser.list";
    public const string BrowserTabs = "browser.tabs";
    public const string BrowserGetTab = "browser.get_tab";
    public const string BrowserOpenTab = "browser.open_tab";
    public const string BrowserCloseTab = "browser.close_tab";
    public const string BrowserNavigate = "browser.navigate";
    public const string BrowserBack = "browser.back";
    public const string BrowserForward = "browser.forward";
    public const string BrowserReload = "browser.reload";
    public const string BrowserQuery = "browser.query";
    public const string BrowserQueryAll = "browser.query_all";
    public const string BrowserClick = "browser.click";
    public const string BrowserFill = "browser.fill";
    public const string BrowserSelect = "browser.select";
    public const string BrowserFocus = "browser.focus";
    public const string BrowserGetText = "browser.get_text";
    public const string BrowserGetDom = "browser.get_dom";
    public const string BrowserGetAccessibilityTree = "browser.get_accessibility_tree";
    public const string BrowserWaitFor = "browser.wait_for";
    public const string BrowserWaitForNavigation = "browser.wait_for_navigation";
    public const string BrowserWaitForNetworkIdle = "browser.wait_for_network_idle";
    public const string BrowserGetDownloads = "browser.get_downloads";

    public const string AdapterList = "adapter.list";
    public const string AdapterCapabilities = "adapter.capabilities";
    public const string AdapterExecute = "adapter.execute";

    public const string BlenderOpen = "blender.open";
    public const string BlenderGetScene = "blender.get_scene";
    public const string BlenderGetObjects = "blender.get_objects";
    public const string BlenderSelectObject = "blender.select_object";
    public const string BlenderExecutePython = "blender.execute_python";
    public const string BlenderExport = "blender.export";
    public const string BlenderSave = "blender.save";
    public const string BlenderBatch = "blender.batch";
    public const string BlenderRender = "blender.render";
    public const string BlenderImportMesh = "blender.import_mesh";
    public const string BlenderCreateMesh = "blender.create_mesh";
    public const string BlenderExportForRoblox = "blender.export_for_roblox";

    public const string VsCodeOpenFile = "vscode.open_file";
    public const string VsCodeOpenFolder = "vscode.open_folder";
    public const string VsCodeExecuteCommand = "vscode.execute_command";
    public const string VsCodeGetWorkspace = "vscode.get_workspace";

    public const string VisualStudioGetSolution = "visualstudio.get_solution";
    public const string VisualStudioBuild = "visualstudio.build";
    public const string VisualStudioOpenFile = "visualstudio.open_file";
    public const string VisualStudioOpenSolution = "visualstudio.open_solution";

    public const string RobloxOpenPlace = "roblox.open_place";
    public const string RobloxPluginPing = "roblox.plugin_ping";
    public const string RobloxGetHierarchy = "roblox.get_hierarchy";
    public const string RobloxGetSelection = "roblox.get_selection";
    public const string RobloxSelect = "roblox.select";
    public const string RobloxSetProperty = "roblox.set_property";
    public const string RobloxCreateInstance = "roblox.create_instance";
    public const string RobloxDestroyInstance = "roblox.destroy_instance";
    public const string RobloxCloneInstance = "roblox.clone_instance";
    public const string RobloxSetParent = "roblox.set_parent";
    public const string RobloxFindInstances = "roblox.find_instances";
    public const string RobloxGetScriptSource = "roblox.get_script_source";
    public const string RobloxSetScriptSource = "roblox.set_script_source";
    public const string RobloxBatch = "roblox.batch";
    public const string RobloxPlaytestStart = "roblox.playtest_start";
    public const string RobloxPlaytestStop = "roblox.playtest_stop";
    public const string RobloxTerrainFillBlock = "roblox.terrain_fill_block";
    public const string RobloxTerrainFillBall = "roblox.terrain_fill_ball";
    public const string RobloxTerrainClear = "roblox.terrain_clear";
    public const string RobloxInsertAsset = "roblox.insert_asset";
    public const string RobloxImportLocalModel = "roblox.import_local_model";
    public const string RobloxPublishPlace = "roblox.publish_place";
    public const string RobloxExecuteLuau = "roblox.execute_luau";

    public const string InputMouseMove = "input.mouse_move";
    public const string InputMouseClick = "input.mouse_click";
    public const string InputMouseDrag = "input.mouse_drag";
    public const string InputScroll = "input.scroll";
    public const string InputKey = "input.key";
    public const string InputHotkey = "input.hotkey";
    public const string InputType = "input.type";

    public const string VisionCaptureScreen = "vision.capture_screen";
    public const string VisionCaptureWindow = "vision.capture_window";
    public const string VisionCaptureRegion = "vision.capture_region";

    public const string SystemUpdateCheck = "system.update.check";
    public const string SystemUpdateApply = "system.update.apply";
    public const string SystemTelemetryGet = "system.telemetry.get";
    public const string SystemTelemetrySet = "system.telemetry.set";
    public const string SystemSecurityReview = "system.security_review";
    public const string SystemIntegrity = "system.integrity";
}

public sealed class BrowserSelector
{
    public string? Css { get; init; }
    public string? Role { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? Placeholder { get; init; }
    public string? Label { get; init; }
    public string? TestId { get; init; }
}

public sealed class BrowserTabRequest
{
    public string? BrowserId { get; init; }
    public string? TabId { get; init; }
    public string? Url { get; init; }
}

public sealed class BrowserNavigateRequest
{
    public string? TabId { get; init; }
    public required string Url { get; init; }
    public int? TimeoutMs { get; init; }
}

public sealed class BrowserQueryRequest
{
    public string? TabId { get; init; }
    public BrowserSelector Selector { get; init; } = new();
}

public sealed class BrowserElementActionRequest
{
    public string? TabId { get; init; }
    public string? ElementId { get; init; }
    public BrowserSelector? Selector { get; init; }
    public string? Value { get; init; }
    public string? Label { get; init; }
}

public sealed class BrowserWaitRequest
{
    public string? TabId { get; init; }
    public BrowserSelector? Selector { get; init; }
    public int? TimeoutMs { get; init; }
    public int? QuietMs { get; init; }
}
