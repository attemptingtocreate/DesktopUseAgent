using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Plans;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Core.Workflow;

public static class CommandFusion
{
    public static IReadOnlyList<PlanStep> FuseSteps(IReadOnlyList<PlanStep> steps)
    {
        if (steps.Count < 2)
        {
            return steps;
        }

        var fused = Fuse(
            steps.Select(s => new BatchCall
            {
                Id = s.Id,
                Method = s.Action,
                Params = s.Args is null ? null : JsonSerializer.SerializeToElement(s.Args)
            }).ToList(),
            laterRefs: SerializeRefs(steps));

        if (fused.Count == steps.Count && fused.Select(c => c.Method).SequenceEqual(steps.Select(s => s.Action)))
        {
            return steps;
        }

        var byId = steps.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var result = new List<PlanStep>(fused.Count);
        foreach (var call in fused)
        {
            var id = call.Id ?? Guid.NewGuid().ToString("N")[..8];
            if (byId.TryGetValue(id, out var original) &&
                string.Equals(original.Action, call.Method, StringComparison.OrdinalIgnoreCase) &&
                call.Method != CommandNames.FilesystemInspect)
            {
                result.Add(original);
                continue;
            }

            result.Add(new PlanStep
            {
                Id = id,
                Action = call.Method,
                Args = ToArgs(call.Params),
                TimeoutMs = originalIf(byId, id)?.TimeoutMs,
                Retries = originalIf(byId, id)?.Retries,
                When = originalIf(byId, id)?.When,
                WaitAfter = originalIf(byId, id)?.WaitAfter,
                OnFailure = originalIf(byId, id)?.OnFailure
            });
        }

        return result;
    }

    public static IReadOnlyList<BatchCall> FuseCalls(IReadOnlyList<BatchCall> calls)
    {
        if (calls.Count < 2)
        {
            return calls;
        }

        return Fuse(calls.ToList(), laterRefs: string.Join('\n', calls.Select(c => c.Params?.GetRawText() ?? "")));
    }

    private static PlanStep? originalIf(Dictionary<string, PlanStep> byId, string id) =>
        byId.TryGetValue(id, out var s) ? s : null;

    private static string SerializeRefs(IReadOnlyList<PlanStep> steps) =>
        string.Join('\n', steps.Select(s =>
            (s.Args is null ? "" : JsonSerializer.Serialize(s.Args)) + (s.When?.Path ?? "") + (s.WaitAfter?.Path ?? "")));

    private static Dictionary<string, JsonElement>? ToArgs(JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in parameters.Value.EnumerateObject())
        {
            args[prop.Name] = prop.Value.Clone();
        }

        return args;
    }

    private static List<BatchCall> Fuse(List<BatchCall> calls, string laterRefs)
    {
        var output = new List<BatchCall>();
        var i = 0;
        while (i < calls.Count)
        {
            if (TryFuseDesktopState(calls, i, laterRefs, out var fusedState, out var consumedState))
            {
                output.Add(fusedState!);
                i += consumedState;
                continue;
            }

            if (TryFuseFilesystem(calls, i, laterRefs, out var fusedFs, out var consumedFs))
            {
                output.Add(fusedFs!);
                i += consumedFs;
                continue;
            }

            output.Add(calls[i]);
            i++;
        }

        return output;
    }

    private static bool TryFuseDesktopState(
        List<BatchCall> calls,
        int start,
        string laterRefs,
        out BatchCall? fused,
        out int consumed)
    {
        fused = null;
        consumed = 0;
        if (start + 1 >= calls.Count)
        {
            return false;
        }

        var a = calls[start];
        var b = calls[start + 1];
        var isState = string.Equals(a.Method, CommandNames.DesktopGetState, StringComparison.OrdinalIgnoreCase);
        var isDescribe = string.Equals(b.Method, CommandNames.DesktopDescribe, StringComparison.OrdinalIgnoreCase);
        if (!isState || !isDescribe)
        {
            return false;
        }

        if (IsReferenced(a.Id, calls, start + 2, laterRefs))
        {
            return false;
        }

        fused = b;
        consumed = 2;
        return true;
    }

    private static bool TryFuseFilesystem(
        List<BatchCall> calls,
        int start,
        string laterRefs,
        out BatchCall? fused,
        out int consumed)
    {
        fused = null;
        consumed = 0;

        var first = calls[start];
        string? listPath = null;
        var statPaths = new List<string>();
        var ids = new List<string?>();
        var j = start;

        if (string.Equals(first.Method, CommandNames.FilesystemList, StringComparison.OrdinalIgnoreCase))
        {
            listPath = GetPath(first.Params) ?? ".";
            ids.Add(first.Id);
            j++;
        }

        while (j < calls.Count && IsStatLike(calls[j].Method))
        {
            var path = GetPath(calls[j].Params);
            if (string.IsNullOrWhiteSpace(path))
            {
                break;
            }

            if (listPath is not null && !IsUnder(path, listPath) &&
                !string.Equals(Path.GetFullPath(path), Path.GetFullPath(listPath), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            statPaths.Add(path);
            ids.Add(calls[j].Id);
            j++;
        }

        var count = j - start;
        if (statPaths.Count == 0 || count < 2)
        {
            return false;
        }

        foreach (var id in ids.Skip(1))
        {
            if (IsReferenced(id, calls, j, laterRefs))
            {
                return false;
            }
        }

        var inspectPath = listPath ?? CommonDirectory(statPaths);
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(inspectPath))
        {
            payload["path"] = inspectPath;
        }

        payload["paths"] = statPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        fused = new BatchCall
        {
            Id = first.Id,
            Method = CommandNames.FilesystemInspect,
            Params = JsonSerializer.SerializeToElement(payload, JsonDefaults.Options)
        };
        consumed = count;
        return true;
    }

    private static bool IsStatLike(string method) =>
        string.Equals(method, CommandNames.FilesystemExists, StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, CommandNames.FilesystemStat, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string parent)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(parent);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? CommonDirectory(IReadOnlyList<string> paths)
    {
        var dirs = paths
            .Select(p =>
            {
                try { return Path.GetDirectoryName(Path.GetFullPath(p)); }
                catch { return null; }
            })
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return dirs.Count == 1 ? dirs[0] : null;
    }

    private static string? GetPath(JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (parameters.Value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
        {
            return path.GetString();
        }

        return null;
    }

    private static bool IsReferenced(string? id, List<BatchCall> calls, int from, string blob)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var needle = "$steps." + id;
        if (blob.Contains(needle, StringComparison.Ordinal))
        {
            // May be a false positive from the fused-away steps themselves; check remaining calls.
        }

        for (var i = from; i < calls.Count; i++)
        {
            var text = calls[i].Params?.GetRawText() ?? "";
            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
