using SemanticDesktop.Browser;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Models;
using SemanticDesktop.UIA.Automation;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Agent;

public sealed class DesktopGraphService
{
    private static readonly HashSet<string> ImportantTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "TabItem",
        "MenuItem", "Hyperlink", "Document", "TreeItem", "ListItem", "SplitButton", "Spinner"
    };

    private readonly WindowService _windows;
    private readonly UIAutomationService _uia;
    private readonly object _gate = new();
    private SemanticDesktopGraph? _cache;
    private SemanticDesktopGraph? _previous;
    private DateTimeOffset _cacheAt;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    public DesktopGraphService(WindowService windows, UIAutomationService uia)
    {
        _windows = windows;
        _uia = uia;
    }

    public async Task<SemanticDesktopGraph> GetGraphAsync(
        bool forceRefresh = false,
        bool includeControls = true,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!forceRefresh && _cache is not null && DateTimeOffset.UtcNow - _cacheAt < CacheTtl)
            {
                return _cache;
            }
        }

        var graph = await BuildAsync(includeControls, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_cache is not null)
            {
                _previous = _cache;
            }

            _cache = graph;
            _cacheAt = DateTimeOffset.UtcNow;
        }

        return graph;
    }

    public DesktopGraphDiff Diff(SemanticDesktopGraph current)
    {
        lock (_gate)
        {
            return DesktopGraphSemantics.Diff(_previous, current);
        }
    }

    public async Task<object> DescribeAsync(bool includeControls = true, CancellationToken cancellationToken = default)
    {
        var graph = await GetGraphAsync(forceRefresh: true, includeControls, cancellationToken).ConfigureAwait(false);
        return new
        {
            text = DesktopGraphSemantics.FormatCompact(graph),
            graph,
            capturedAt = graph.CapturedAt
        };
    }

    private async Task<SemanticDesktopGraph> BuildAsync(bool includeControls, CancellationToken ct)
    {
        var windows = (await _windows.ListAsync(ct).ConfigureAwait(false))
            .Where(w => !string.IsNullOrWhiteSpace(w.Title))
            .OrderByDescending(w => w.Foreground)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        var foreground = windows.FirstOrDefault(w => w.Foreground) ?? windows.FirstOrDefault();
        var apps = DesktopGraphSemantics.GroupApplications(windows).ToList();
        var tabs = BrowserDiscovery.TryListLivePages();
        DesktopGraphSemantics.AttachBrowserTabs(apps, tabs);

        IReadOnlyList<GraphControl> controls = Array.Empty<GraphControl>();
        if (includeControls && foreground is not null && !foreground.Minimized)
        {
            controls = await TryGetImportantControlsAsync(foreground.Id, ct).ConfigureAwait(false);
        }

        return new SemanticDesktopGraph
        {
            CapturedAt = DateTimeOffset.UtcNow,
            FocusedApplication = foreground is null
                ? null
                : DesktopGraphSemantics.FriendlyAppName(foreground.Process, foreground.Title),
            FocusedWindowId = foreground?.Id,
            Applications = apps,
            Windows = windows.Select(w => new GraphWindow
            {
                Id = w.Id,
                Title = w.Title,
                Process = w.Process,
                Pid = w.Pid,
                Foreground = w.Foreground,
                Minimized = w.Minimized
            }).ToList(),
            BrowserTabs = tabs,
            ImportantControls = controls
        };
    }

    private async Task<GraphControl[]> TryGetImportantControlsAsync(string windowId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            var elements = await _uia.FindAsync(new UIFindQuery
            {
                WindowId = windowId,
                Depth = 3,
                MaxResults = 24,
                Selector = new UIFindSelector { Offscreen = false }
            }, timeout.Token).ConfigureAwait(false);

            return elements
                .Where(e => !string.IsNullOrWhiteSpace(e.Name)
                            && (e.ControlType is null || ImportantTypes.Contains(e.ControlType)))
                .Take(12)
                .Select(e => new GraphControl
                {
                    Id = e.Id,
                    WindowId = windowId,
                    Name = DesktopGraphSemantics.Truncate(e.Name, 80),
                    ControlType = e.ControlType,
                    AutomationId = e.AutomationId,
                    Enabled = e.Enabled
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<GraphControl>();
        }
    }
}
