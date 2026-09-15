using System.Text.Json;
using SemanticDesktop.App.Models;

namespace SemanticDesktop.App.Persistence;

public sealed class ConversationStore
{
    private readonly string? _dataRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, Conversation> _memory = new(StringComparer.Ordinal);

    public ConversationStore(string? dataRoot)
    {
        _dataRoot = dataRoot;
        if (_dataRoot is not null)
        {
            Directory.CreateDirectory(ConversationsDir);
        }
    }

    private string ConversationsDir => Path.Combine(_dataRoot!, "conversations");

    public IReadOnlyList<ConversationListItem> List()
    {
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                return _memory.Values
                    .OrderByDescending(c => c.UpdatedAt)
                    .Select(ToListItem)
                    .ToList();
            }

            if (!Directory.Exists(ConversationsDir))
            {
                return Array.Empty<ConversationListItem>();
            }

            var items = new List<ConversationListItem>();
            foreach (var file in Directory.EnumerateFiles(ConversationsDir, "*.json"))
            {
                try
                {
                    var convo = ReadFile(file);
                    if (convo is not null)
                    {
                        items.Add(ToListItem(convo));
                    }
                }
                catch
                {
                    // skip corrupt files
                }
            }

            return items.OrderByDescending(i => i.UpdatedAt).ToList();
        }
    }

    public Conversation? Get(string id)
    {
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                return _memory.TryGetValue(id, out var convo) ? StoreJson.Clone(convo) : null;
            }

            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return null;
            }

            return ReadFile(path);
        }
    }

    public Conversation Save(Conversation conversation)
    {
        if (string.IsNullOrWhiteSpace(conversation.Id))
        {
            conversation.Id = "conv_" + Guid.NewGuid().ToString("N");
        }

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        var clone = StoreJson.Clone(conversation);
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                _memory[clone.Id] = StoreJson.Clone(clone);
                return clone;
            }

            Directory.CreateDirectory(ConversationsDir);
            var json = JsonSerializer.Serialize(clone, StoreJson.FileOptions);
            File.WriteAllText(PathFor(clone.Id), json);
            return clone;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                return _memory.Remove(id);
            }

            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    public Conversation? UpdateTitle(string id, string title)
    {
        var convo = Get(id);
        if (convo is null)
        {
            return null;
        }

        convo.Title = title;
        return Save(convo);
    }

    private string PathFor(string id) => Path.Combine(ConversationsDir, $"{id}.json");

    private static Conversation? ReadFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Conversation>(json, StoreJson.FileOptions);
    }

    private static ConversationListItem ToListItem(Conversation c) => new()
    {
        Id = c.Id,
        Title = c.Title,
        UpdatedAt = c.UpdatedAt,
        SelectedProviderId = c.SelectedProviderId
    };
}
