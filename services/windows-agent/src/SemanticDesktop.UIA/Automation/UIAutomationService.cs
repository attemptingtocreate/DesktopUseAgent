using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Contracts;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Results;
using SemanticDesktop.Core.Targets;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.UIA.Automation;

public sealed class UIAutomationService : IUIAutomationService, IDisposable
{
    private readonly HandleRegistry _handles;
    private readonly WindowService _windows;
    private readonly ElementCache _cache;
    private readonly UIA3Automation? _automation;
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(10);

    public UIAutomationService(HandleRegistry handles, WindowService windows)
    {
        _handles = handles;
        _windows = windows;
        _cache = new ElementCache();
        try
        {
            _automation = new UIA3Automation();
            Available = true;
        }
        catch
        {
            _automation = null;
            Available = false;
        }
    }

    public bool Available { get; }

    public int LastElementsInspected { get; private set; }
    public bool? LastCacheHit { get; private set; }

    public Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken) =>
        _windows.ListAsync(cancellationToken);

    public async Task<UIElement?> ResolveAsync(SemanticTarget target, CancellationToken cancellationToken)
    {
        var matches = await FindInternalAsync(new UIFindQuery
        {
            WindowId = target.WindowId,
            RootId = target.RuntimeId,
            Selector = new UIFindSelector
            {
                AutomationId = target.AutomationId,
                Name = target.Name,
                ControlType = target.ControlType,
                ClassName = target.ClassName,
                FrameworkId = target.FrameworkId
            },
            MaxResults = 5
        }, cancellationToken).ConfigureAwait(false);

        if (target.RuntimeId is not null &&
            _handles.TryResolve<CachedElement>(target.RuntimeId, out var cached, out _) &&
            cached.Element.IsAvailable)
        {
            LastCacheHit = true;
            return ToUiElement(target.RuntimeId, cached.Element, target.WindowId);
        }

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1 && target.Index is null)
        {
            return null;
        }

        var index = target.Index ?? 0;
        if (index < 0 || index >= matches.Count)
        {
            return null;
        }

        return matches[index];
    }

    public async Task<IReadOnlyList<UIElement>> FindAsync(UIFindQuery query, CancellationToken cancellationToken)
    {
        return await FindInternalAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public Task<UITree> GetTreeAsync(UITreeQuery query, CancellationToken cancellationToken)
    {
        return RunWithTimeout(ct =>
        {
            LastCacheHit = false;
            LastElementsInspected = 0;
            var root = ResolveRoot(query.WindowId, query.RootId);
            var maxNodes = query.MaxNodes ?? 200;
            var depth = query.Depth ?? 3;
            var count = 0;
            var node = BuildTree(root, query, depth, maxNodes, ref count, query.WindowId);
            LastElementsInspected = count;
            return Task.FromResult(new UITree
            {
                Root = node,
                NodeCount = count,
                TreeMode = query.TreeMode
            });
        }, cancellationToken);
    }

    public Task<ActionResult> InvokeAsync(ElementHandle element, CancellationToken cancellationToken)
    {
        return RunWithTimeout(ct =>
        {
            var automationElement = RequireElement(element.Id);
            LastElementsInspected = 1;
            LastCacheHit = _cache.Contains(element.Id);

            if (automationElement.Patterns.Invoke.IsSupported)
            {
                automationElement.Patterns.Invoke.Pattern.Invoke();
                return Task.FromResult(new ActionResult { Success = true, Message = "Invoked" });
            }

            if (automationElement.Patterns.Toggle.IsSupported)
            {
                automationElement.Patterns.Toggle.Pattern.Toggle();
                return Task.FromResult(new ActionResult { Success = true, Message = "Toggled" });
            }

            if (automationElement.Patterns.SelectionItem.IsSupported)
            {
                automationElement.Patterns.SelectionItem.Pattern.Select();
                return Task.FromResult(new ActionResult { Success = true, Message = "Selected" });
            }

            if (automationElement.Patterns.ExpandCollapse.IsSupported)
            {
                var state = automationElement.Patterns.ExpandCollapse.Pattern.ExpandCollapseState;
                if (state == ExpandCollapseState.Collapsed)
                {
                    automationElement.Patterns.ExpandCollapse.Pattern.Expand();
                }
                else
                {
                    automationElement.Patterns.ExpandCollapse.Pattern.Collapse();
                }

                return Task.FromResult(new ActionResult { Success = true, Message = "ExpandCollapse" });
            }

            automationElement.Focus();
            return Task.FromResult(new ActionResult
            {
                Success = false,
                Message = ErrorCodes.PatternUnavailable
            });
        }, cancellationToken);
    }

    public Task<ActionResult> SetValueAsync(ElementHandle element, string value, CancellationToken cancellationToken)
    {
        return RunWithTimeout(ct =>
        {
            var automationElement = RequireElement(element.Id);
            LastElementsInspected = 1;
            LastCacheHit = _cache.Contains(element.Id);

            if (automationElement.Patterns.Value.IsSupported)
            {
                automationElement.Patterns.Value.Pattern.SetValue(value);
                return Task.FromResult(new ActionResult { Success = true, Message = "Value set" });
            }

            if (automationElement.Patterns.LegacyIAccessible.IsSupported)
            {
                automationElement.Patterns.LegacyIAccessible.Pattern.SetValue(value);
                return Task.FromResult(new ActionResult { Success = true, Message = "Legacy value set" });
            }

            automationElement.Focus();
            automationElement.AsTextBox()?.Enter(value);
            if (automationElement.ControlType == ControlType.Edit ||
                automationElement.ControlType == ControlType.Document)
            {
                return Task.FromResult(new ActionResult { Success = true, Message = "Text entered" });
            }

            return Task.FromResult(new ActionResult
            {
                Success = false,
                Message = ErrorCodes.PatternUnavailable
            });
        }, cancellationToken);
    }

    public Task<string?> GetTextAsync(ElementHandle element, CancellationToken cancellationToken)
    {
        return RunWithTimeout(ct =>
        {
            var automationElement = RequireElement(element.Id);
            LastElementsInspected = 1;
            LastCacheHit = _cache.Contains(element.Id);
            return Task.FromResult(ReadText(automationElement));
        }, cancellationToken);
    }

    public bool TryDescribe(string elementId, out string? controlType, out string? name, out string? automationId, out bool isPassword)
    {
        controlType = null;
        name = null;
        automationId = null;
        isPassword = false;
        try
        {
            var automationElement = RequireElement(elementId);
            controlType = automationElement.ControlType.ToString();
            name = automationElement.Properties.Name.TryGetValue(out var n) ? n : null;
            automationId = automationElement.Properties.AutomationId.TryGetValue(out var a) ? a : null;
            isPassword = automationElement.Properties.IsPassword.TryGetValue(out var p) && p;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _automation?.Dispose();
    }

    private async Task<IReadOnlyList<UIElement>> FindInternalAsync(UIFindQuery query, CancellationToken cancellationToken)
    {
        return await RunWithTimeout(ct =>
        {
            LastCacheHit = false;
            LastElementsInspected = 0;
            if (_automation is null)
            {
                return Task.FromResult<IReadOnlyList<UIElement>>(Array.Empty<UIElement>());
            }

            var root = ResolveRoot(query.WindowId, query.RootId);
            var maxResults = query.MaxResults ?? 25;
            var depth = query.Depth ?? 12;
            var matches = new List<UIElement>();
            Walk(root, query, depth, maxResults, matches, query.WindowId, 0);
            return Task.FromResult<IReadOnlyList<UIElement>>(matches);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Walk(
        AutomationElement current,
        UIFindQuery query,
        int maxDepth,
        int maxResults,
        List<UIElement> matches,
        string? windowId,
        int depth)
    {
        if (matches.Count >= maxResults || depth > maxDepth)
        {
            return;
        }

        LastElementsInspected++;
        if (Matches(current, query.Selector))
        {
            var id = RegisterElement(current, windowId);
            matches.Add(ToUiElement(id, current, windowId));
            if (matches.Count >= maxResults)
            {
                return;
            }
        }

        foreach (var child in SafeChildren(current))
        {
            Walk(child, query, maxDepth, maxResults, matches, windowId, depth + 1);
            if (matches.Count >= maxResults)
            {
                return;
            }
        }
    }

    private UITreeNode BuildTree(
        AutomationElement current,
        UITreeQuery query,
        int remainingDepth,
        int maxNodes,
        ref int count,
        string? windowId)
    {
        count++;
        LastElementsInspected++;
        var id = RegisterElement(current, windowId);
        var summary = ToSummary(id, current, includeBounds: query.IncludeBounds);
        var children = new List<UITreeNode>();

        if (remainingDepth > 0 && count < maxNodes)
        {
            foreach (var child in SafeChildren(current))
            {
                if (count >= maxNodes)
                {
                    break;
                }

                if (query.InteractiveOnly && !IsInteractive(child))
                {
                    // still descend to find interactive descendants one level deeper
                    if (remainingDepth > 1)
                    {
                        var nested = BuildTree(child, query, remainingDepth - 1, maxNodes, ref count, windowId);
                        if (nested.Children.Count > 0 || IsInteractive(child))
                        {
                            children.Add(nested);
                        }
                    }

                    continue;
                }

                children.Add(BuildTree(child, query, remainingDepth - 1, maxNodes, ref count, windowId));
            }
        }

        return new UITreeNode
        {
            Element = summary,
            Children = children
        };
    }

    private AutomationElement ResolveRoot(string? windowId, string? rootId)
    {
        if (_automation is null)
        {
            throw new InvalidOperationException($"{ErrorCodes.Unsupported}: UI Automation is unavailable.");
        }

        if (!string.IsNullOrWhiteSpace(rootId))
        {
            if (_handles.TryResolve<CachedElement>(rootId, out var cached, out _) && cached.Element.IsAvailable)
            {
                LastCacheHit = true;
                return cached.Element;
            }

            throw new InvalidOperationException(ErrorCodes.StaleTarget);
        }

        if (!string.IsNullOrWhiteSpace(windowId))
        {
            if (!_windows.TryGetHwnd(windowId, out var hwnd))
            {
                throw new InvalidOperationException(ErrorCodes.StaleTarget);
            }

            return _automation.FromHandle(hwnd);
        }

        return _automation.GetDesktop();
    }

    private AutomationElement RequireElement(string elementId)
    {
        if (_cache.TryGet(elementId, out var cached) && cached.Element.IsAvailable)
        {
            return cached.Element;
        }

        if (_handles.TryResolve<CachedElement>(elementId, out var handleCached, out _) &&
            handleCached.Element.IsAvailable)
        {
            _cache.Set(elementId, handleCached);
            return handleCached.Element;
        }

        throw new InvalidOperationException(ErrorCodes.StaleTarget);
    }

    private string RegisterElement(AutomationElement element, string? windowId)
    {
        var runtimeKey = GetRuntimeKey(element);
        var stableId = "uia_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(runtimeKey)))[..16].ToLowerInvariant();

        if (_handles.TryGetElementByRuntimeKey(runtimeKey, out var indexedId) &&
            _handles.TryGet(indexedId, out var indexedEntry) &&
            indexedEntry.NativeKey is CachedElement indexedCached &&
            indexedCached.RuntimeKey == runtimeKey &&
            indexedCached.Element.IsAvailable)
        {
            _cache.Set(indexedId, indexedCached);
            return indexedId;
        }

        if (_handles.TryGet(stableId, out var stableEntry) &&
            stableEntry.NativeKey is CachedElement stableCached &&
            stableCached.RuntimeKey == runtimeKey &&
            stableCached.Element.IsAvailable)
        {
            _cache.Set(stableId, stableCached);
            return stableId;
        }

        var wrapped = new CachedElement(element, runtimeKey, windowId);
        var allocated = _handles.Allocate(HandleKind.Element, wrapped, new Dictionary<string, object?>
        {
            ["runtimeKey"] = runtimeKey,
            ["windowId"] = windowId
        }, preferredId: stableId);
        _cache.Set(allocated, wrapped);
        return allocated;
    }

    private static string GetRuntimeKey(AutomationElement element)
    {
        try
        {
            var pid = element.Properties.ProcessId.TryGetValue(out var p) ? p : 0;
            var autoId = element.Properties.AutomationId.TryGetValue(out var a) ? a : "";
            var name = element.Properties.Name.TryGetValue(out var n) ? n : "";
            var control = element.ControlType.ToString();
            var runtimeId = string.Join("-", element.Properties.RuntimeId.TryGetValue(out var r) ? r : Array.Empty<int>());
            return $"{pid}|{control}|{autoId}|{name}|{runtimeId}";
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private static bool Matches(AutomationElement element, UIFindSelector selector)
    {
        if (selector.AutomationId is not null)
        {
            var autoId = element.Properties.AutomationId.TryGetValue(out var v) ? v : null;
            if (!string.Equals(autoId, selector.AutomationId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (selector.Name is not null)
        {
            var name = element.Properties.Name.TryGetValue(out var v) ? v : null;
            if (!string.Equals(name, selector.Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (selector.NameContains is not null)
        {
            var name = element.Properties.Name.TryGetValue(out var v) ? v : "";
            if (name is null || name.IndexOf(selector.NameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        if (selector.ControlType is not null)
        {
            if (!ControlTypeMatches(element.ControlType, selector.ControlType))
            {
                return false;
            }
        }

        if (selector.ClassName is not null)
        {
            var className = element.Properties.ClassName.TryGetValue(out var v) ? v : null;
            if (!string.Equals(className, selector.ClassName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (selector.FrameworkId is not null)
        {
            var framework = element.Properties.FrameworkId.TryGetValue(out var v) ? v : null;
            if (!string.Equals(framework, selector.FrameworkId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (selector.Enabled is not null)
        {
            var enabled = element.Properties.IsEnabled.TryGetValue(out var v) && v;
            if (enabled != selector.Enabled.Value)
            {
                return false;
            }
        }

        if (selector.Offscreen is not null)
        {
            var offscreen = element.Properties.IsOffscreen.TryGetValue(out var v) && v;
            if (offscreen != selector.Offscreen.Value)
            {
                return false;
            }
        }

        return selector.AutomationId is not null
               || selector.Name is not null
               || selector.NameContains is not null
               || selector.ControlType is not null
               || selector.ClassName is not null
               || selector.FrameworkId is not null
               || selector.Enabled is not null
               || selector.Offscreen is not null;
    }

    private static bool ControlTypeMatches(ControlType actual, string expected)
    {
        var normalized = expected.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(actual.ToString(), expected, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actual.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // common aliases from CLI / MCP
        return expected.ToLowerInvariant() switch
        {
            "document" => actual == ControlType.Document,
            "edit" or "textbox" => actual == ControlType.Edit,
            "button" => actual == ControlType.Button,
            "checkbox" => actual == ControlType.CheckBox,
            "radio" or "radiobutton" => actual == ControlType.RadioButton,
            "combo" or "combobox" => actual == ControlType.ComboBox,
            "list" => actual == ControlType.List,
            "listitem" => actual == ControlType.ListItem,
            "tree" => actual == ControlType.Tree,
            "tab" => actual == ControlType.Tab,
            "tabitem" => actual == ControlType.TabItem,
            "menu" => actual == ControlType.Menu,
            "menuitem" => actual == ControlType.MenuItem,
            "progressbar" => actual == ControlType.ProgressBar,
            "scrollbar" => actual == ControlType.ScrollBar,
            "window" => actual == ControlType.Window,
            _ => false
        };
    }

    private static bool IsInteractive(AutomationElement element)
    {
        var type = element.ControlType;
        return type == ControlType.Button
               || type == ControlType.Edit
               || type == ControlType.CheckBox
               || type == ControlType.RadioButton
               || type == ControlType.ComboBox
               || type == ControlType.ListItem
               || type == ControlType.MenuItem
               || type == ControlType.TabItem
               || type == ControlType.Hyperlink
               || type == ControlType.Slider
               || type == ControlType.Document
               || element.Patterns.Invoke.IsSupported
               || element.Patterns.Value.IsSupported;
    }

    private UIElement ToUiElement(string id, AutomationElement element, string? windowId)
    {
        return new UIElement
        {
            Id = id,
            Name = element.Properties.Name.TryGetValue(out var name) ? name : null,
            AutomationId = element.Properties.AutomationId.TryGetValue(out var autoId) ? autoId : null,
            ControlType = element.ControlType.ToString(),
            ClassName = element.Properties.ClassName.TryGetValue(out var className) ? className : null,
            FrameworkId = element.Properties.FrameworkId.TryGetValue(out var framework) ? framework : null,
            ProcessId = element.Properties.ProcessId.TryGetValue(out var pid) ? pid : 0,
            WindowId = windowId,
            Enabled = element.Properties.IsEnabled.TryGetValue(out var enabled) && enabled,
            Focused = element.Properties.HasKeyboardFocus.TryGetValue(out var focused) && focused,
            Offscreen = element.Properties.IsOffscreen.TryGetValue(out var offscreen) && offscreen,
            Bounds = GetBounds(element),
            SupportedPatterns = GetPatterns(element),
            Value = element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value : null,
            Text = ReadText(element)
        };
    }

    private static UIElementSummary ToSummary(string id, AutomationElement element, bool includeBounds)
    {
        return new UIElementSummary
        {
            Id = id,
            Name = element.Properties.Name.TryGetValue(out var name) ? name : null,
            AutomationId = element.Properties.AutomationId.TryGetValue(out var autoId) ? autoId : null,
            ControlType = element.ControlType.ToString(),
            ClassName = element.Properties.ClassName.TryGetValue(out var className) ? className : null,
            FrameworkId = element.Properties.FrameworkId.TryGetValue(out var framework) ? framework : null,
            Enabled = element.Properties.IsEnabled.TryGetValue(out var enabled) && enabled,
            Focused = element.Properties.HasKeyboardFocus.TryGetValue(out var focused) && focused,
            Offscreen = element.Properties.IsOffscreen.TryGetValue(out var offscreen) && offscreen,
            Bounds = includeBounds ? GetBounds(element) : null,
            SupportedPatterns = GetPatterns(element)
        };
    }

    private static Rect? GetBounds(AutomationElement element)
    {
        try
        {
            var rect = element.BoundingRectangle;
            if (rect.IsEmpty)
            {
                return null;
            }

            return new Rect
            {
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height
            };
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetPatterns(AutomationElement element)
    {
        var patterns = new List<string>();
        void Add(string name, bool supported)
        {
            if (supported)
            {
                patterns.Add(name);
            }
        }

        Add("Invoke", element.Patterns.Invoke.IsSupported);
        Add("Value", element.Patterns.Value.IsSupported);
        Add("Text", element.Patterns.Text.IsSupported);
        Add("Toggle", element.Patterns.Toggle.IsSupported);
        Add("Selection", element.Patterns.Selection.IsSupported);
        Add("SelectionItem", element.Patterns.SelectionItem.IsSupported);
        Add("ExpandCollapse", element.Patterns.ExpandCollapse.IsSupported);
        Add("Scroll", element.Patterns.Scroll.IsSupported);
        Add("ScrollItem", element.Patterns.ScrollItem.IsSupported);
        Add("RangeValue", element.Patterns.RangeValue.IsSupported);
        Add("Window", element.Patterns.Window.IsSupported);
        Add("Transform", element.Patterns.Transform.IsSupported);
        Add("Grid", element.Patterns.Grid.IsSupported);
        Add("GridItem", element.Patterns.GridItem.IsSupported);
        Add("Table", element.Patterns.Table.IsSupported);
        Add("TableItem", element.Patterns.TableItem.IsSupported);
        Add("LegacyIAccessible", element.Patterns.LegacyIAccessible.IsSupported);
        return patterns;
    }

    private static string? ReadText(AutomationElement element)
    {
        if (element.Patterns.Value.IsSupported)
        {
            return element.Patterns.Value.Pattern.Value;
        }

        if (element.Patterns.Text.IsSupported)
        {
            try
            {
                return element.Patterns.Text.Pattern.DocumentRange.GetText(4096);
            }
            catch
            {
                // ignore
            }
        }

        return element.Properties.Name.TryGetValue(out var name) ? name : null;
    }

    private static IEnumerable<AutomationElement> SafeChildren(AutomationElement element)
    {
        try
        {
            return element.FindAllChildren();
        }
        catch
        {
            return Array.Empty<AutomationElement>();
        }
    }

    private async Task<T> RunWithTimeout<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_defaultTimeout);
        try
        {
            return await Task.Run(async () => await action(linked.Token).ConfigureAwait(false), linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(ErrorCodes.Timeout);
        }
    }
}

internal sealed class CachedElement
{
    public CachedElement(AutomationElement element, string runtimeKey, string? windowId)
    {
        Element = element;
        RuntimeKey = runtimeKey;
        WindowId = windowId;
        LastSeen = DateTimeOffset.UtcNow;
    }

    public AutomationElement Element { get; }
    public string RuntimeKey { get; }
    public string? WindowId { get; }
    public DateTimeOffset LastSeen { get; set; }
}

internal sealed class ElementCache
{
    private readonly Dictionary<string, CachedElement> _items = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(2);

    public bool Contains(string id) => _items.ContainsKey(id);

    public bool TryGet(string id, out CachedElement element)
    {
        if (_items.TryGetValue(id, out element!))
        {
            if (DateTimeOffset.UtcNow - element.LastSeen <= _ttl && element.Element.IsAvailable)
            {
                element.LastSeen = DateTimeOffset.UtcNow;
                return true;
            }

            _items.Remove(id);
        }

        element = null!;
        return false;
    }

    public void Set(string id, CachedElement element)
    {
        element.LastSeen = DateTimeOffset.UtcNow;
        _items[id] = element;
    }
}
