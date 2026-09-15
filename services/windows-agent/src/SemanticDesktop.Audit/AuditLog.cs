using System.Collections.Concurrent;
using System.Text.Json;
using SemanticDesktop.Core.Security;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Audit;

public sealed class AuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    private readonly int _capacity;
    private readonly string? _directory;
    private readonly TimeSpan _retention;
    private readonly object _fileGate = new();

    public AuditLog(int capacity = 5000, string? directory = null, TimeSpan? retention = null)
    {
        _capacity = capacity;
        _directory = directory;
        _retention = retention ?? TimeSpan.FromDays(14);
        if (!string.IsNullOrWhiteSpace(_directory))
        {
            Directory.CreateDirectory(_directory);
            Prune();
        }
    }

    public void Record(AuditEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }

        WriteFile(entry);
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

    public int Prune(DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(_directory) || !Directory.Exists(_directory))
        {
            return 0;
        }

        var cutoff = (now ?? DateTimeOffset.UtcNow) - _retention;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "audit-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch
            {
                // best-effort retention
            }
        }

        return removed;
    }

    private void WriteFile(AuditEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_directory))
        {
            return;
        }

        try
        {
            var file = Path.Combine(_directory, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var line = JsonSerializer.Serialize(entry, JsonDefaults.Options) + Environment.NewLine;
            lock (_fileGate)
            {
                File.AppendAllText(file, line);
            }
        }
        catch
        {
            // audit must never break the agent
        }
    }
}
