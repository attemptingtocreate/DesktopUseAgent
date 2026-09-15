using SemanticDesktop.App.Mcp;

namespace SemanticDesktop.App.Persistence;

public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string? DefaultModel { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> EnabledMcpIds { get; set; } = new() { McpIds.Builtin };
    public Dictionary<string, string>? Options { get; set; }
}
