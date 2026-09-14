using System.Collections.Concurrent;
using SemanticDesktop.Core.Security;

namespace SemanticDesktop.Audit;

public sealed class AuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    private readonly int _capacity;

    public AuditLog(int capacity = 5000)
    {
        _capacity = capacity;
    }

    public void Record(AuditEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<AuditEntry> List(int take = 100, string? sessionId = null)
    {
        var query = _entries.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            query = query.Where(e => e.SessionId == sessionId);
        }

        return query.Reverse().Take(Math.Clamp(take, 1, 1000)).ToList();
    }
}
