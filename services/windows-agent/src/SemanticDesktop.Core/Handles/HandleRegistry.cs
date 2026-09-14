using System.Security.Cryptography;

namespace SemanticDesktop.Core.Handles;

public enum HandleKind
{
    Window,
    Element,
    Process
}

public sealed class HandleRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HandleEntry> _entries = new(StringComparer.Ordinal);

    public string Allocate(HandleKind kind, object nativeKey, Dictionary<string, object?>? metadata = null, string? preferredId = null)
    {
        lock (_gate)
        {
            if (preferredId is not null && _entries.TryGetValue(preferredId, out var existing))
            {
                existing.NativeKey = nativeKey;
                existing.LastSeen = DateTimeOffset.UtcNow;
                if (metadata is not null)
                {
                    foreach (var kv in metadata)
                    {
                        existing.Metadata[kv.Key] = kv.Value;
                    }
                }

                return preferredId;
            }

            var id = preferredId ?? CreateId(kind);
            _entries[id] = new HandleEntry(kind, nativeKey, DateTimeOffset.UtcNow, metadata);
            return id;
        }
    }

    public bool TryGet(string id, out HandleEntry entry)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out entry!);
        }
    }

    public bool TryResolve<T>(string id, out T native, out string? errorCode)
    {
        native = default!;
        errorCode = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                errorCode = Errors.ErrorCodes.StaleTarget;
                return false;
            }

            if (entry.NativeKey is not T typed)
            {
                errorCode = Errors.ErrorCodes.StaleTarget;
                return false;
            }

            native = typed;
            entry.LastSeen = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _entries.Remove(id);
        }
    }

    public void RemoveWhere(Func<HandleEntry, bool> predicate)
    {
        lock (_gate)
        {
            var keys = _entries.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList();
            foreach (var key in keys)
            {
                _entries.Remove(key);
            }
        }
    }

    public IReadOnlyList<string> ListIds(HandleKind kind)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Value.Kind == kind).Select(e => e.Key).ToList();
        }
    }

    public static string CreateId(HandleKind kind)
    {
        var prefix = kind switch
        {
            HandleKind.Window => "win_",
            HandleKind.Element => "uia_",
            HandleKind.Process => "proc_",
            _ => "id_"
        };

        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class HandleEntry
{
    public HandleEntry(HandleKind kind, object nativeKey, DateTimeOffset createdAt, Dictionary<string, object?>? metadata)
    {
        Kind = kind;
        NativeKey = nativeKey;
        CreatedAt = createdAt;
        LastSeen = createdAt;
        Metadata = metadata ?? new Dictionary<string, object?>();
    }

    public HandleKind Kind { get; }
    public object NativeKey { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastSeen { get; set; }
    public Dictionary<string, object?> Metadata { get; }
}
