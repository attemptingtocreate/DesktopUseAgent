namespace SemanticDesktop.Core.Security;

public enum PermissionDecisionKind
{
    Allow,
    Ask,
    Deny
}

public enum PermissionGrantScope
{
    Once,
    Session,
    Always
}

public enum RiskClass
{
    Read,
    LowRiskWrite,
    HighRiskWrite,
    Destructive,
    Privileged
}

public static class Capabilities
{
    public const string DesktopObserve = "desktop.observe";
    public const string WindowObserve = "window.observe";
    public const string WindowControl = "window.control";
    public const string UiObserve = "ui.observe";
    public const string UiInteract = "ui.interact";
    public const string FilesystemRead = "filesystem.read";
    public const string FilesystemWrite = "filesystem.write";
    public const string FilesystemDelete = "filesystem.delete";
    public const string ProcessObserve = "process.observe";
    public const string ProcessLaunch = "process.launch";
    public const string ProcessTerminate = "process.terminate";
    public const string ShellExecute = "shell.execute";
    public const string ClipboardRead = "clipboard.read";
    public const string ClipboardWrite = "clipboard.write";
    public const string SystemPower = "system.power";
    public const string PlanExecute = "plan.execute";
    public const string BrowserObserve = "browser.observe";
    public const string BrowserInteract = "browser.interact";
    public const string AdapterObserve = "adapter.observe";
    public const string AdapterInteract = "adapter.interact";
    public const string InputKeyboard = "input.keyboard";
    public const string InputMouse = "input.mouse";
    public const string VisionCapture = "vision.capture";
    public const string SystemAdmin = "system.elevated";
}

public sealed class AgentSession
{
    public required string SessionId { get; init; }
    public required string ClientId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool AutoApproveAsk { get; set; }
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public Dictionary<string, PermissionDecisionKind> CapabilityOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SessionGrants { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PermissionEvaluation
{
    public required string Capability { get; init; }
    public required PermissionDecisionKind Decision { get; init; }
    public required RiskClass Risk { get; init; }
    public string? Reason { get; init; }
    public string? Target { get; init; }
    public string? Application { get; init; }
}

public sealed class ApprovalRequest
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string Action { get; init; }
    public required string Capability { get; init; }
    public required RiskClass Risk { get; init; }
    public string? Target { get; init; }
    public string? Application { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "pending";
}

public sealed class AuditEntry
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Client { get; init; }
    public required string SessionId { get; init; }
    public required string Action { get; init; }
    public string? Target { get; init; }
    public required string PermissionDecision { get; init; }
    public required long DurationMs { get; init; }
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? RequestId { get; init; }
}

public static class SensitiveRedactor
{
    public const string Redacted = "<redacted>";

    public static bool LooksSensitive(string? controlType, string? name, string? automationId, string? className)
    {
        static bool Has(string? value, params string[] tokens) =>
            value is not null && tokens.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));

        return Has(controlType, "Password")
               || Has(name, "password", "passwd", "secret", "api key", "token", "credit card", "ssn", "seed")
               || Has(automationId, "password", "passwd", "secret", "token", "apikey")
               || Has(className, "PasswordBox");
    }

    public static string RedactValue(string? value) =>
        string.IsNullOrEmpty(value) ? value ?? string.Empty : Redacted;
}
