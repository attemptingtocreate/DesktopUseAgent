namespace SemanticDesktop.App.Mcp;

public static class McpIds
{
    public const string Builtin = "desktopuseagent";
    public const string BuiltinDisplayName = "DesktopUseAgent";
}

public sealed class McpConfiguration
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public List<string>? EnvKeys { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsBuiltin { get; set; }
}

public sealed class McpServerStatus
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "disconnected";
    public int ToolCount { get; init; }
    public string? Error { get; init; }
    public bool IsBuiltin { get; init; }
}

public sealed class McpGatewayStatus
{
    public bool AvailableAsStdioModule { get; init; }
    public string? Path { get; init; }
    public string? EntryPath { get; init; }
    public string? ProductVersion { get; init; }
    public string Note { get; init; } = "Native chat does not require starting the Node MCP gateway (Mode B: Cursor spawns stdio).";
}
