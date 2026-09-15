using System.Text.Json;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Agent;

public sealed class SemanticCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public SemanticCache(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromSeconds(2);

    public bool TryGet(string method, JsonElement? parameters, out string json)
    {
        var key = Key(method, parameters);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.At < _ttl)
            {
                json = entry.Json;
                return true;
            }
        }

        json = "";
        return false;
    }

    public void Set(string method, JsonElement? parameters, object result)
    {
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        var key = Key(method, parameters);
        lock (_gate)
        {
            _entries[key] = new Entry(json, DateTimeOffset.UtcNow);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static string Key(string method, JsonElement? parameters) =>
        method + ":" + (parameters is null ? "" : parameters.Value.GetRawText());

    private readonly record struct Entry(string Json, DateTimeOffset At);
}
