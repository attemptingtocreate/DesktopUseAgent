using System.Text;
using System.Text.RegularExpressions;

namespace SemanticDesktop.Core.Models;

public static class DesktopGraphSemantics
{
    private static readonly HashSet<string> TextSkipKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "kind", "tabs", "activeUrl"
    };

    public static IReadOnlyList<GraphApplication> GroupApplications(IReadOnlyList<WindowInfo> windows)
    {
        return windows
            .GroupBy(w => NormalizeProcess(w.Process), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var sample = g.FirstOrDefault(w => w.Foreground) ?? g.First();
                var titles = g.Select(x => x.Title).ToList();
                return new GraphApplication
                {
                    Name = FriendlyAppName(sample.Process, sample.Title),
                    Process = sample.Process,
                    Pid = sample.Pid,
                    Focused = g.Any(w => w.Foreground),
                    State = InferState(sample.Process, sample.Title, titles),
                    WindowIds = g.Select(w => w.Id).Take(8).ToArray()
                };
            })
            .OrderByDescending(a => a.Focused)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    public static Dictionary<string, string?> InferState(string process, string title, IReadOnlyList<string> titles)
    {
        var state = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var proc = process ?? "";
        title ??= "";

        if (IsBlender(proc))
        {
            var file = ExtractFileName(title, ".blend")
                       ?? ExtractSuffixTitle(title, " - Blender")
                       ?? titles.Select(t => ExtractFileName(t, ".blend")).FirstOrDefault(f => f is not null);
            if (file is not null)
            {
                state["file"] = file.TrimEnd('*');
            }
        }
        else if (IsVisualStudio(proc, title))
        {
            string? sln = null;
            foreach (var t in titles.Prepend(title))
            {
                sln = ExtractFileName(t, ".sln");
                if (sln is not null)
                {
                    break;
                }
            }

            sln ??= ExtractSuffixTitle(title, " - Microsoft Visual Studio");
            if (sln is not null)
            {
                state["solution"] = sln;
            }
        }
        else if (IsVsCode(proc, title))
        {
            var parts = title.Split(new[] { " - ", " — " }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !parts[0].Equals("Visual Studio Code", StringComparison.OrdinalIgnoreCase))
            {
                state["file"] = parts[0];
                if (parts.Length >= 3)
                {
                    state["folder"] = parts[1];
                }
            }
        }
        else if (IsRobloxStudio(proc, title))
        {
            var place = ExtractFileName(title, ".rbxlx")
                        ?? ExtractFileName(title, ".rbxl")
                        ?? titles.Select(t => ExtractFileName(t, ".rbxlx") ?? ExtractFileName(t, ".rbxl"))
                            .FirstOrDefault(f => f is not null);
            place ??= ExtractSuffixTitle(title, " - Roblox Studio");
            if (place is not null)
            {
                state["place"] = place.TrimEnd('*');
            }
        }
        else if (IsBrowserProcess(proc))
        {
            state["kind"] = "browser";
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            state["title"] = Truncate(title, 120);
        }

        return state;
    }

    public static void AttachBrowserTabs(IList<GraphApplication> apps, IReadOnlyList<GraphBrowserTab> tabs)
    {
        if (tabs.Count == 0)
        {
            return;
        }

        foreach (var app in apps.Where(a => IsBrowserProcess(a.Process ?? "") || IsBrowserName(a.Name)))
        {
            var owned = tabs.Where(t => TabBelongsTo(t, app)).ToList();
            var source = owned.Count > 0 ? owned : tabs;
            var titles = source
                .Select(t => t.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Take(5);
            app.State["tabs"] = string.Join(", ", titles!);
            var active = source.FirstOrDefault(t => t.Active) ?? source.FirstOrDefault();
            if (active?.Url is not null)
            {
                app.State["activeUrl"] = Truncate(active.Url, 160);
            }
        }
    }

    public static string FormatCompact(SemanticDesktopGraph graph)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Desktop");
        if (!string.IsNullOrWhiteSpace(graph.FocusedApplication))
        {
            sb.AppendLine($"  focused: {graph.FocusedApplication}");
        }

        foreach (var app in graph.Applications.Take(12))
        {
            sb.AppendLine(app.Name);
            if (app.Focused)
            {
                sb.AppendLine("    focused: true");
            }

            foreach (var kv in app.State.Where(kv => !TextSkipKeys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)).Take(6))
            {
                sb.AppendLine($"    {kv.Key}: {kv.Value}");
            }

            var tabs = graph.BrowserTabs.Where(t => TabBelongsTo(t, app)).Take(8).ToList();
            if (tabs.Count == 0 && IsBrowserProcess(app.Process ?? "") && graph.BrowserTabs.Count > 0
                && graph.Applications.Count(a => IsBrowserProcess(a.Process ?? "")) == 1)
            {
                tabs = graph.BrowserTabs.Take(8).ToList();
            }

            if (tabs.Count > 0)
            {
                sb.AppendLine("    tabs:");
                foreach (var tab in tabs)
                {
                    var label = tab.Title ?? tab.Url ?? tab.Id;
                    sb.AppendLine($"      {(tab.Active ? "* " : "")}{label}");
                }
            }
        }

        if (graph.ImportantControls.Count > 0)
        {
            sb.AppendLine("  importantControls:");
            foreach (var c in graph.ImportantControls.Take(12))
            {
                sb.AppendLine($"    - {c.ControlType}:{c.Name ?? c.AutomationId ?? c.Id}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FriendlyAppName(string process, string title)
    {
        var p = process ?? "";
        if (IsBlender(p)) return "Blender";
        if (IsRobloxStudio(p, title)) return "Roblox Studio";
        if (IsVisualStudio(p, title)) return "Visual Studio";
        if (IsVsCode(p, title)) return "VS Code";
        if (p.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (p.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (p.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (p.Contains("brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (!string.IsNullOrWhiteSpace(title))
        {
            var parts = title.Split(new[] { " - ", " — " }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                return Truncate(parts[^1], 40)!;
            }
        }

        return string.IsNullOrWhiteSpace(p) ? "unknown" : Path.GetFileNameWithoutExtension(p);
    }

    public static bool IsBrowserProcess(string process) =>
        process.Contains("chrome", StringComparison.OrdinalIgnoreCase)
        || process.Contains("msedge", StringComparison.OrdinalIgnoreCase)
        || process.Contains("firefox", StringComparison.OrdinalIgnoreCase)
        || process.Contains("brave", StringComparison.OrdinalIgnoreCase);

    public static bool TabBelongsTo(GraphBrowserTab tab, GraphApplication app)
    {
        if (string.IsNullOrWhiteSpace(tab.Browser))
        {
            return IsBrowserProcess(app.Process ?? "") || IsBrowserName(app.Name);
        }

        return string.Equals(tab.Browser, app.Name, StringComparison.OrdinalIgnoreCase)
               || (app.Process?.Contains("chrome", StringComparison.OrdinalIgnoreCase) == true
                   && tab.Browser.Contains("chrome", StringComparison.OrdinalIgnoreCase))
               || (app.Process?.Contains("msedge", StringComparison.OrdinalIgnoreCase) == true
                   && tab.Browser.Contains("edge", StringComparison.OrdinalIgnoreCase))
               || (app.Process?.Contains("brave", StringComparison.OrdinalIgnoreCase) == true
                   && tab.Browser.Contains("brave", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBrowserName(string? name) =>
        name is not null && (
            name.Equals("Chrome", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Edge", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Brave", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Firefox", StringComparison.OrdinalIgnoreCase));

    private static bool IsBlender(string process) =>
        process.Contains("blender", StringComparison.OrdinalIgnoreCase);

    private static bool IsRobloxStudio(string process, string title) =>
        process.Contains("RobloxStudioBeta", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Roblox Studio", StringComparison.OrdinalIgnoreCase);

    private static bool IsVisualStudio(string process, string title) =>
        process.Contains("devenv", StringComparison.OrdinalIgnoreCase)
        || (title.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase)
            && !title.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase)
            && !IsVsCode(process, ""));

    private static bool IsVsCode(string process, string title)
    {
        var name = Path.GetFileNameWithoutExtension(process ?? "");
        return name.Equals("Code", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Code - Insiders", StringComparison.OrdinalIgnoreCase)
               || title.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProcess(string process) =>
        string.IsNullOrWhiteSpace(process) ? "unknown" : process.Trim();

    private static string? ExtractSuffixTitle(string title, string marker)
    {
        var idx = title.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx <= 0)
        {
            return null;
        }

        return Truncate(title[..idx].Trim(), 120);
    }

    private static string? ExtractFileName(string title, string extension)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var pattern = $@"[\w.\- ]+{Regex.Escape(extension)}";
        var match = Regex.Match(title, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Value.Trim() : null;
    }

    public static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

    public static DesktopGraphDiff Diff(SemanticDesktopGraph? before, SemanticDesktopGraph after)
    {
        before ??= new SemanticDesktopGraph { CapturedAt = after.CapturedAt };
        var beforeWindows = IndexWindows(before.Windows);
        var afterWindows = IndexWindows(after.Windows);

        var addedWindows = after.Windows.Where(w => !beforeWindows.ContainsKey(WindowKey(w))).ToList();
        var removedWindows = before.Windows.Where(w => !afterWindows.ContainsKey(WindowKey(w))).ToList();
        var changedWindows = after.Windows.Where(w =>
        {
            if (!beforeWindows.TryGetValue(WindowKey(w), out var prev))
            {
                return false;
            }

            return !string.Equals(prev.Title, w.Title, StringComparison.Ordinal)
                   || prev.Foreground != w.Foreground
                   || prev.Minimized != w.Minimized
                   || prev.Maximized != w.Maximized
                   || prev.MonitorIndex != w.MonitorIndex
                   || !BoundsEqual(prev.Bounds, w.Bounds);
        }).ToList();

        var beforeApps = before.Applications.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterApps = after.Applications.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var beforeTabs = before.BrowserTabs.Select(TabKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterTabs = after.BrowserTabs.Select(TabKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new DesktopGraphDiff
        {
            From = before.CapturedAt,
            To = after.CapturedAt,
            FocusChanged = !string.Equals(before.FocusedWindowId, after.FocusedWindowId, StringComparison.Ordinal)
                           || !string.Equals(before.FocusedApplication, after.FocusedApplication, StringComparison.OrdinalIgnoreCase),
            PreviousFocus = before.FocusedApplication,
            CurrentFocus = after.FocusedApplication,
            AddedWindows = addedWindows,
            RemovedWindows = removedWindows,
            ChangedWindows = changedWindows,
            AddedApplications = afterApps.Except(beforeApps, StringComparer.OrdinalIgnoreCase).ToArray(),
            RemovedApplications = beforeApps.Except(afterApps, StringComparer.OrdinalIgnoreCase).ToArray(),
            AddedTabs = after.BrowserTabs.Where(t => !beforeTabs.Contains(TabKey(t))).ToList(),
            RemovedTabs = before.BrowserTabs.Where(t => !afterTabs.Contains(TabKey(t))).ToList()
        };
    }

    private static bool BoundsEqual(Rect? left, Rect? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return Math.Abs(left.X - right.X) < 0.5
               && Math.Abs(left.Y - right.Y) < 0.5
               && Math.Abs(left.Width - right.Width) < 0.5
               && Math.Abs(left.Height - right.Height) < 0.5;
    }

    private static Dictionary<string, GraphWindow> IndexWindows(IReadOnlyList<GraphWindow> windows) =>
        windows
            .GroupBy(WindowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static string WindowKey(GraphWindow w) =>
        !string.IsNullOrWhiteSpace(w.Id) ? w.Id : $"{w.Process}|{w.Pid}|{w.Title}";

    private static string TabKey(GraphBrowserTab t) =>
        t.Url ?? t.Title ?? t.Id;
}
