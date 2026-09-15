using System.Text.Json;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Tools;

namespace SemanticDesktop.App.Mcp;

public sealed class McpManager : IMcpHost
{
    private readonly McpConfigStore _store;
    private readonly CredentialStore _credentials;
    private readonly ProviderConfigStore _providers;
    private readonly IAgentRpc? _rpc;
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _toolCounts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<McpConfiguration, IMcpClient>? _clientFactory;

    public McpManager(
        McpConfigStore store,
        CredentialStore credentials,
        ProviderConfigStore providers,
        IAgentRpc? rpc = null,
        Func<McpConfiguration, IMcpClient>? clientFactory = null)
    {
        _store = store;
        _credentials = credentials;
        _providers = providers;
        _rpc = rpc;
        _clientFactory = clientFactory;
    }

    public IReadOnlyList<McpConfiguration> List() => _store.List();

    public McpConfiguration Add(McpConfiguration config, string? defaultProviderId = null)
    {
        config.IsBuiltin = false;
        if (string.IsNullOrWhiteSpace(config.Type))
        {
            config.Type = "stdio";
        }

        var saved = _store.Upsert(config);
        var assignTo = defaultProviderId ?? _providers.List().FirstOrDefault()?.Id;
        if (!string.IsNullOrWhiteSpace(assignTo))
        {
            _providers.AssignMcp(assignTo, saved.Id, enabled: true);
        }

        return saved;
    }

    public McpConfiguration Edit(McpConfiguration config) => _store.Upsert(config);

    public McpConfiguration? Enable(string id, bool enabled)
    {
        var existing = _store.Get(id);
        if (existing is null)
        {
            return null;
        }

        existing.Enabled = enabled;
        return _store.Upsert(existing);
    }

    public bool Remove(string id)
    {
        var removed = _store.Remove(id);
        if (removed)
        {
            foreach (var provider in _providers.List())
            {
                _providers.AssignMcp(provider.Id, id, enabled: false);
            }
        }

        return removed;
    }

    public async Task<McpServerStatus> TestConnectionAsync(string id, CancellationToken cancellationToken = default)
    {
        var cfg = _store.Get(id);
        if (cfg is null)
        {
            return new McpServerStatus { Id = id, State = "error", Error = "Unknown MCP server." };
        }

        if (cfg.IsBuiltin || string.Equals(cfg.Type, "builtin", StringComparison.OrdinalIgnoreCase))
        {
            var ping = await PingAgentAsync(cancellationToken).ConfigureAwait(false);
            var status = new McpServerStatus
            {
                Id = cfg.Id,
                Name = cfg.Name,
                IsBuiltin = true,
                ToolCount = ToolCatalog.ComputerControlTools.Count,
                State = ping ? "connected" : "disconnected",
                Error = ping ? null : "Windows Agent ping failed."
            };
            Record(status);
            return status;
        }

        try
        {
            var tools = await ListToolsAsync(id, cancellationToken).ConfigureAwait(false);
            var status = new McpServerStatus
            {
                Id = cfg.Id,
                Name = cfg.Name,
                State = "connected",
                ToolCount = tools.Count,
                IsBuiltin = false
            };
            Record(status);
            return status;
        }
        catch (Exception ex)
        {
            var status = new McpServerStatus
            {
                Id = cfg.Id,
                Name = cfg.Name,
                State = "error",
                Error = ex.Message,
                IsBuiltin = false
            };
            Record(status);
            return status;
        }
    }

    public async Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(string mcpId, CancellationToken cancellationToken = default)
    {
        var cfg = _store.Get(mcpId);
        if (cfg is null)
        {
            throw new InvalidOperationException($"Unknown MCP '{mcpId}'.");
        }

        if (cfg.IsBuiltin || string.Equals(cfg.Type, "builtin", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                _errors.Remove(mcpId);
                _toolCounts[mcpId] = ToolCatalog.ComputerControlTools.Count;
            }

            return ToolCatalog.ComputerControlTools;
        }

        await using var client = CreateClient(cfg);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _errors.Remove(mcpId);
                _toolCounts[mcpId] = tools.Count;
            }

            return tools.Select(t => new AgentToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                JsonSchema = t.JsonSchema,
                Source = mcpId
            }).ToList();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _errors[mcpId] = ex.Message;
            }

            throw;
        }
    }

    public async Task<ToolRouterResult> CallToolAsync(string mcpId, string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (ToolCatalog.IsComputerControl(name) || string.Equals(mcpId, McpIds.Builtin, StringComparison.OrdinalIgnoreCase))
        {
            return ToolRouterResult.Denied("Computer-control tools must go through AgentRpc, not external MCP.");
        }

        var cfg = _store.Get(mcpId) ?? throw new InvalidOperationException($"Unknown MCP '{mcpId}'.");
        await using var client = CreateClient(cfg);
        return await client.CallToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
    }

    public McpServerStatus GetStatus(string mcpId)
    {
        var cfg = _store.Get(mcpId);
        if (cfg is null)
        {
            return new McpServerStatus { Id = mcpId, State = "error", Error = "Unknown MCP server." };
        }

        lock (_gate)
        {
            _errors.TryGetValue(mcpId, out var error);
            _toolCounts.TryGetValue(mcpId, out var count);
            if (cfg.IsBuiltin)
            {
                count = ToolCatalog.ComputerControlTools.Count;
            }

            return new McpServerStatus
            {
                Id = cfg.Id,
                Name = cfg.Name,
                IsBuiltin = cfg.IsBuiltin,
                ToolCount = count,
                State = error is null ? (cfg.Enabled ? "ready" : "disabled") : "error",
                Error = error
            };
        }
    }

    public IReadOnlyDictionary<string, string> Errors
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_errors, StringComparer.Ordinal);
            }
        }
    }

    public void SetError(string mcpId, string? error)
    {
        RecordError(mcpId, error);
    }

    public void RecordError(string mcpId, string? error)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                _errors.Remove(mcpId);
            }
            else
            {
                _errors[mcpId] = error;
            }
        }
    }

    private IMcpClient CreateClient(McpConfiguration cfg)
    {
        if (_clientFactory is not null)
        {
            return _clientFactory(cfg);
        }

        if (!string.Equals(cfg.Type, "stdio", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(cfg.Command))
        {
            throw new InvalidOperationException($"MCP '{cfg.Id}' is not a stdio server with a command.");
        }

        Dictionary<string, string>? env = null;
        if (cfg.EnvKeys is { Count: > 0 })
        {
            env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in cfg.EnvKeys)
            {
                var value = _credentials.Get(CredentialStore.McpEnv(cfg.Id, key));
                if (value is not null)
                {
                    env[key] = value;
                }
            }
        }

        return new StdioMcpClient(cfg.Command, cfg.Args, env);
    }

    private async Task<bool> PingAgentAsync(CancellationToken cancellationToken)
    {
        if (_rpc is null)
        {
            return false;
        }

        try
        {
            var result = await _rpc.CallAsync("system.ping", new { }, cancellationToken).ConfigureAwait(false);
            return result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                || result.TryGetProperty("data", out var data) && data.TryGetProperty("ok", out var dok) && dok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private void Record(McpServerStatus status)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(status.Error))
            {
                _errors.Remove(status.Id);
            }
            else
            {
                _errors[status.Id] = status.Error;
            }

            _toolCounts[status.Id] = status.ToolCount;
        }
    }
}

public sealed class FakeMcpHost : IMcpHost, IAsyncDisposable
{
    public Dictionary<string, List<AgentToolDefinition>> ToolsByServer { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Exception> ListErrors { get; } = new(StringComparer.Ordinal);
    public List<(string McpId, string Name)> Calls { get; } = new();
    public Dictionary<string, McpServerStatus> Statuses { get; } = new(StringComparer.Ordinal);
    public Func<string, string, JsonElement, Task<ToolRouterResult>>? CallHandler { get; set; }

    public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(string mcpId, CancellationToken cancellationToken = default)
    {
        if (ListErrors.TryGetValue(mcpId, out var ex))
        {
            Statuses[mcpId] = new McpServerStatus { Id = mcpId, State = "error", Error = ex.Message };
            throw ex;
        }

        if (string.Equals(mcpId, McpIds.Builtin, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ToolCatalog.ComputerControlTools);
        }

        var tools = ToolsByServer.TryGetValue(mcpId, out var list) ? list : new List<AgentToolDefinition>();
        return Task.FromResult<IReadOnlyList<AgentToolDefinition>>(tools);
    }

    public Task<ToolRouterResult> CallToolAsync(string mcpId, string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (ToolCatalog.IsComputerControl(name))
        {
            return Task.FromResult(ToolRouterResult.Denied("External MCP cannot execute computer-control tools."));
        }

        Calls.Add((mcpId, name));
        if (CallHandler is not null)
        {
            return CallHandler(mcpId, name, arguments);
        }

        return Task.FromResult(ToolRouterResult.Ok($"{mcpId}:{name} ok"));
    }

    public McpServerStatus GetStatus(string mcpId)
    {
        if (ListErrors.TryGetValue(mcpId, out var ex))
        {
            return new McpServerStatus { Id = mcpId, State = "error", Error = ex.Message };
        }

        return Statuses.TryGetValue(mcpId, out var status)
            ? status
            : new McpServerStatus { Id = mcpId, State = "ready" };
    }

    public void RecordError(string mcpId, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            Statuses.Remove(mcpId);
            return;
        }

        Statuses[mcpId] = new McpServerStatus { Id = mcpId, State = "error", Error = error };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
