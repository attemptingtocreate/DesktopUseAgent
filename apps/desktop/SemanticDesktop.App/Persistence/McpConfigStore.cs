using System.Text.Json;
using SemanticDesktop.App.Mcp;

namespace SemanticDesktop.App.Persistence;

public sealed class McpConfigStore
{
    private readonly string? _dataRoot;
    private readonly object _gate = new();
    private readonly List<McpConfiguration> _memory = new();

    public McpConfigStore(string? dataRoot)
    {
        _dataRoot = dataRoot;
        if (_dataRoot is not null)
        {
            Directory.CreateDirectory(_dataRoot);
        }

        EnsureBuiltin();
    }

    private string FilePath => Path.Combine(_dataRoot!, "mcp-servers.json");

    public IReadOnlyList<McpConfiguration> List()
    {
        lock (_gate)
        {
            return StoreJson.Clone(WithBuiltin(LoadUnsafe()));
        }
    }

    public McpConfiguration? Get(string id)
    {
        lock (_gate)
        {
            var found = WithBuiltin(LoadUnsafe()).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return found is null ? null : StoreJson.Clone(found);
        }
    }

    public McpConfiguration Upsert(McpConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
        {
            config.Id = "mcp_" + Guid.NewGuid().ToString("N")[..12];
        }

        if (string.Equals(config.Id, McpIds.Builtin, StringComparison.OrdinalIgnoreCase))
        {
            config.IsBuiltin = true;
            config.Type = "builtin";
            config.Name = string.IsNullOrWhiteSpace(config.Name) ? McpIds.BuiltinDisplayName : config.Name;
        }

        var clone = StoreJson.Clone(config);
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
        if (string.Equals(id, McpIds.Builtin, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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

    private void EnsureBuiltin()
    {
        lock (_gate)
        {
            var list = LoadUnsafe();
            if (!list.Any(c => string.Equals(c.Id, McpIds.Builtin, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(Builtin());
                PersistUnsafe(list);
            }
        }
    }

    private List<McpConfiguration> LoadUnsafe()
    {
        if (_dataRoot is null)
        {
            return _memory;
        }

        if (!File.Exists(FilePath))
        {
            return new List<McpConfiguration>();
        }

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<McpConfiguration>>(json, StoreJson.FileOptions) ?? new List<McpConfiguration>();
    }

    private void PersistUnsafe(List<McpConfiguration> list)
    {
        if (_dataRoot is null)
        {
            var copies = list.Select(StoreJson.Clone).ToList();
            _memory.Clear();
            _memory.AddRange(copies);
            return;
        }

        Directory.CreateDirectory(_dataRoot!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(WithBuiltin(list), StoreJson.FileOptions));
    }

    private static List<McpConfiguration> WithBuiltin(List<McpConfiguration> list)
    {
        if (!list.Any(c => string.Equals(c.Id, McpIds.Builtin, StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(0, Builtin());
        }

        return list;
    }

    public static McpConfiguration Builtin() => new()
    {
        Id = McpIds.Builtin,
        Name = McpIds.BuiltinDisplayName,
        Type = "builtin",
        Enabled = true,
        IsBuiltin = true
    };
}
