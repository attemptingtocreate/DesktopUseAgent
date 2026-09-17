using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters;

public delegate ProcessInfo? AdapterProcessResolver(string? processId, string? windowId);

public sealed class AdapterRegistry
{
    private readonly List<IApplicationAdapter> _adapters = new();
    private AdapterProcessResolver? _processResolver;

    public AdapterRegistry(IEnumerable<IApplicationAdapter>? adapters = null)
    {
        if (adapters is not null)
        {
            _adapters.AddRange(adapters);
        }
    }

    public void Register(IApplicationAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters.RemoveAll(a => string.Equals(a.Id, adapter.Id, StringComparison.OrdinalIgnoreCase));
        _adapters.Add(adapter);
    }

    public IReadOnlyList<string> ListIds() =>
        _adapters.Select(a => a.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public IApplicationAdapter? Get(string adapterId) =>
        _adapters.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase));

    public IApplicationAdapter? Resolve(ProcessInfo process) =>
        _adapters.FirstOrDefault(a => a.CanHandle(process));

    public void SetProcessResolver(AdapterProcessResolver? resolver) => _processResolver = resolver;

    public IApplicationAdapter? ResolveByAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return null;
        }

        var prefix = action.Split('.', 2)[0];
        return _adapters.FirstOrDefault(a =>
            string.Equals(a.Id, prefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Id, MapPrefix(prefix), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ApplicationCapabilities>> ListCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var list = new List<ApplicationCapabilities>();
        foreach (var adapter in _adapters)
        {
            list.Add(await adapter.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false));
        }

        return list;
    }

    public async Task<AdapterResult> ExecuteAsync(
        string? adapterId,
        AdapterCommand command,
        CancellationToken cancellationToken)
    {
        IApplicationAdapter? adapter = null;
        if (!string.IsNullOrWhiteSpace(adapterId))
        {
            adapter = Get(adapterId);
        }

        adapter ??= ResolveByAction(command.Action);
        if (adapter is null && _processResolver is not null)
        {
            var process = _processResolver(command.ProcessId, command.WindowId);
            if (process is not null)
            {
                adapter = Resolve(process);
            }
        }

        if (adapter is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterNotFound, $"No adapter for '{command.Action}'.");
        }

        return await adapter.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static string MapPrefix(string prefix) => prefix.ToLowerInvariant() switch
    {
        "vscode" => "vscode",
        "visualstudio" => "visualstudio",
        "blender" => "blender",
        "roblox" => "roblox",
        "discord" => "discord",
        _ => prefix
    };
}
