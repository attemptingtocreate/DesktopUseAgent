namespace SemanticDesktop.Core.Commands;

public sealed class WindowFocusRequest
{
    public required string WindowId { get; init; }
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
    public const string WindowList = "window.list";
    public const string WindowFocus = "window.focus";
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
    public const string ProcessList = "process.list";
    public const string DesktopGetState = "desktop.get_state";
    public const string DesktopGetCapabilities = "desktop.get_capabilities";
    public const string WindowWaitFor = "window.wait_for";
    public const string UiWaitFor = "ui.wait_for";
    public const string SessionCreate = "session.create";
    public const string SessionGet = "session.get";
    public const string PermissionApprove = "permission.approve";
    public const string PermissionDeny = "permission.deny";
    public const string PermissionPending = "permission.pending";
    public const string AuditList = "audit.list";
    public const string SystemEmergencyStop = "system.emergency_stop";
    public const string SystemEmergencyStopClear = "system.emergency_stop.clear";
}
