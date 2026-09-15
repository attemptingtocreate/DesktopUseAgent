namespace SemanticDesktop.App.Persistence;

public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public AgentSettings Agents { get; set; } = new();
    public ComputerControlSettings ComputerControl { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public OpenAiTunnelSettings OpenAiTunnel { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
}

public sealed class OpenAiTunnelSettings
{
    public bool Enabled { get; set; }
    public string ProfileName { get; set; } = "desktopuseagent";
    public string? ProfileDirectory { get; set; }
    public string? TunnelClientPath { get; set; }
}

public sealed class GeneralSettings
{
    public bool LaunchAtStartup { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.LeaveAgentRunning;
    public bool Notifications { get; set; } = true;
}

public enum CloseBehavior
{
    ExitAll,
    LeaveAgentRunning
}

public sealed class AgentSettings
{
    public string? DefaultProviderId { get; set; }
    public string? DefaultModel { get; set; }
}

public sealed class ComputerControlSettings
{
    public bool Enabled { get; set; } = true;
    public string EmergencyStopShortcut { get; set; } = "Ctrl+Alt+Shift+Esc";
    public bool PreferSemanticOverFallback { get; set; } = true;
}

public sealed class PrivacySettings
{
    public int ConversationRetentionDays { get; set; } = 90;
    public bool LocalLogs { get; set; } = true;
    public bool TelemetryEnabled { get; set; }
}

public sealed class AdvancedSettings
{
    public int MaxToolIterations { get; set; } = 25;
    public bool ShowToolSummaries { get; set; } = true;
}
