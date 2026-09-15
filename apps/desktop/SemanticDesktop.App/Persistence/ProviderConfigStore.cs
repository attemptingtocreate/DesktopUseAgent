using System.Text.Json;

namespace SemanticDesktop.App.Persistence;

public sealed class ProviderConfigStore
{
    private readonly string? _dataRoot;
    private readonly object _gate = new();
    private readonly List<ProviderConfig> _memory = new();

    public ProviderConfigStore(string? dataRoot)
    {
        _dataRoot = dataRoot;
        if (_dataRoot is not null)
        {
            Directory.CreateDirectory(_dataRoot);
        }
    }

    private string FilePath => Path.Combine(_dataRoot!, "providers.json");

    public IReadOnlyList<ProviderConfig> List()
    {
        lock (_gate)
        {
            return StoreJson.Clone(LoadUnsafe());
        }
    }

    public ProviderConfig? Get(string id)
    {
        lock (_gate)
        {
            var found = LoadUnsafe().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return found is null ? null : Copy(found);
        }
    }

    public ProviderConfig Upsert(ProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
        {
            config.Id = "prov_" + Guid.NewGuid().ToString("N")[..12];
        }

        var clone = Copy(config);
        lock (_gate)
        {
            var list = LoadUnsafe();
            var idx = list.FindIndex(p => string.Equals(p.Id, clone.Id, StringComparison.Ordinal));
            if (idx >= 0)
            {
                list[idx] = clone;
            }
            else
            {
                list.Add(clone);
            }

            PersistUnsafe(list);
            return StoreJson.Clone(clone);
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var list = LoadUnsafe();
            var removed = list.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                PersistUnsafe(list);
            }

            return removed;
        }
    }

    public void AssignMcp(string providerId, string mcpId, bool enabled)
    {
        lock (_gate)
        {
            var list = LoadUnsafe();
            var provider = list.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
            if (provider is null)
            {
                return;
            }

            provider.EnabledMcpIds ??= new List<string>();
            if (enabled)
            {
                if (!provider.EnabledMcpIds.Contains(mcpId, StringComparer.Ordinal))
                {
                    provider.EnabledMcpIds.Add(mcpId);
                }
            }
            else
            {
                provider.EnabledMcpIds.RemoveAll(x => string.Equals(x, mcpId, StringComparison.Ordinal));
            }

            PersistUnsafe(list);
        }
    }

    private List<ProviderConfig> LoadUnsafe()
    {
        if (_dataRoot is null)
        {
            return _memory;
        }

        if (!File.Exists(FilePath))
        {
            return new List<ProviderConfig>();
        }

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<ProviderConfig>>(json, StoreJson.FileOptions) ?? new List<ProviderConfig>();
    }

    private void PersistUnsafe(List<ProviderConfig> list)
    {
        if (_dataRoot is null)
        {
            var copies = list.Select(Copy).ToList();
            _memory.Clear();
            _memory.AddRange(copies);
            return;
        }

        Directory.CreateDirectory(_dataRoot!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, StoreJson.FileOptions));
    }

    private static ProviderConfig Copy(ProviderConfig config) => new()
    {
        Id = config.Id,
        Type = config.Type,
        DisplayName = config.DisplayName,
        BaseUrl = config.BaseUrl,
        DefaultModel = config.DefaultModel,
        Enabled = config.Enabled,
        EnabledMcpIds = config.EnabledMcpIds?.ToList() ?? new List<string>(),
        Options = config.Options is null ? null : new Dictionary<string, string>(config.Options)
    };
}
