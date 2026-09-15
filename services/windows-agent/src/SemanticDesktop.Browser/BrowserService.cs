using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Results;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Browser;

public sealed class BrowserService : IDisposable
{
    private static readonly string SelectorHelperJs = """
function __sdMatch(el, sel) {
  if (!el || el.nodeType !== 1) return false;
  if (sel.css) {
    try { if (!el.matches(sel.css)) return false; } catch (e) { return false; }
  }
  if (sel.testId) {
    if ((el.getAttribute('data-testid') || '') !== sel.testId) return false;
  }
  if (sel.placeholder) {
    if ((el.getAttribute('placeholder') || '') !== sel.placeholder) return false;
  }
  if (sel.label) {
    let ok = false;
    if (el.labels) {
      for (const lab of el.labels) {
        if ((lab.textContent || '').trim() === sel.label) { ok = true; break; }
      }
    }
    if (!ok) {
      const id = el.getAttribute('id');
      if (id) {
        const lab = document.querySelector('label[for="' + CSS.escape(id) + '"]');
        if (lab && (lab.textContent || '').trim() === sel.label) ok = true;
      }
    }
    if (!ok) return false;
  }
  if (sel.text) {
    const t = (el.innerText || el.textContent || '').trim();
    if (!t.includes(sel.text)) return false;
  }
  if (sel.role) {
    const role = (el.getAttribute('role') || __sdImplicitRole(el) || '').toLowerCase();
    if (role !== String(sel.role).toLowerCase()) return false;
  }
  if (sel.name) {
    const name = __sdAccessibleName(el);
    if (name !== sel.name) return false;
  }
  return true;
}
function __sdImplicitRole(el) {
  const tag = el.tagName.toLowerCase();
  if (tag === 'button') return 'button';
  if (tag === 'a' && el.hasAttribute('href')) return 'link';
  if (tag === 'input') {
    const t = (el.getAttribute('type') || 'text').toLowerCase();
    if (t === 'button' || t === 'submit' || t === 'reset') return 'button';
    if (t === 'checkbox') return 'checkbox';
    if (t === 'radio') return 'radio';
    return 'textbox';
  }
  if (tag === 'select') return 'combobox';
  if (tag === 'textarea') return 'textbox';
  if (tag === 'img') return 'img';
  return '';
}
function __sdAccessibleName(el) {
  const aria = el.getAttribute('aria-label');
  if (aria) return aria.trim();
  if (el.labels && el.labels.length) return (el.labels[0].textContent || '').trim();
  const id = el.getAttribute('id');
  if (id) {
    const lab = document.querySelector('label[for="' + CSS.escape(id) + '"]');
    if (lab) return (lab.textContent || '').trim();
  }
  const titled = el.getAttribute('title');
  if (titled) return titled.trim();
  if (el.tagName.toLowerCase() === 'img') return (el.getAttribute('alt') || '').trim();
  return (el.innerText || el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 200);
}
function __sdQuery(sel, all) {
  const out = [];
  let candidates;
  if (sel.css) {
    try { candidates = Array.from(document.querySelectorAll(sel.css)); }
    catch (e) { candidates = []; }
  } else if (sel.testId) {
    candidates = Array.from(document.querySelectorAll('[data-testid]'));
  } else {
    candidates = Array.from(document.querySelectorAll('body *'));
  }
  for (const el of candidates) {
    if (__sdMatch(el, sel)) {
      out.push(el);
      if (!all) break;
    }
  }
  return all ? out : (out[0] || null);
}
function __sdDescribe(el, id) {
  if (!el) return null;
  el.setAttribute('data-sd-ref', id);
  return {
    id: id,
    tag: el.tagName.toLowerCase(),
    role: el.getAttribute('role') || __sdImplicitRole(el) || null,
    name: __sdAccessibleName(el) || null,
    text: ((el.innerText || el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 200)) || null,
    value: ('value' in el ? String(el.value ?? '') : null)
  };
}
""";

    private readonly HandleRegistry _handles;
    private readonly object _gate = new();
    private readonly List<BrowserDownloadInfo> _downloads = new();
    private readonly ConcurrentDictionary<string, string> _tabSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tabTargets = new(StringComparer.Ordinal);

    private CdpConnection? _cdp;
    private Process? _chromeProcess;
    private string? _userDataDir;
    private string? _browserId;
    private string? _activeTabId;
    private int _debugPort;
    private int _inFlight;
    private bool _disposed;

    public BrowserService(HandleRegistry? handles = null)
    {
        _handles = handles ?? new HandleRegistry();
    }

    public HandleRegistry Handles => _handles;

    public ToolResult<object> List(string? requestId = null)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = requestId ?? Guid.NewGuid().ToString("N");
        var discovered = BrowserDiscovery.Discover();
        var list = discovered.Select(d =>
        {
            var id = _handles.Allocate(
                HandleKind.Browser,
                d.Path,
                new Dictionary<string, object?>
                {
                    ["name"] = d.Name,
                    ["path"] = d.Path,
                    ["debugPort"] = d.DebugPort,
                    ["running"] = d.Running
                },
                preferredId: StableBrowserId(d.Path));
            return new BrowserInfo
            {
                Id = id,
                Name = d.Name,
                Path = d.Path,
                DebugPort = d.DebugPort,
                Running = d.Running
            };
        }).ToList();

        return Ok(CommandNames.BrowserList, rid, started, list, list.Count);
    }

    public async Task<ToolResult<object>> EnsureSessionAsync(
        bool headless = false,
        string? executablePath = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            await EnsureConnectedAsync(headless, executablePath, cancellationToken).ConfigureAwait(false);
            return Ok(CommandNames.BrowserList, rid, started, new
            {
                browserId = _browserId,
                debugPort = _debugPort,
                connected = true
            }, 1);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserList, rid, started, ErrorCodes.ProcessFailed, ex.Message, retryable: true);
        }
    }

    public async Task<ToolResult<object>> TabsAsync(string? browserId = null, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            await EnsureConnectedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var tabs = await ListTabsInternalAsync(cancellationToken).ConfigureAwait(false);
            return Ok(CommandNames.BrowserTabs, rid, started, tabs, tabs.Count);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserTabs, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> GetTabAsync(string tabId, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            await EnsureConnectedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var tabs = await ListTabsInternalAsync(cancellationToken).ConfigureAwait(false);
            var tab = tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab is null)
            {
                return Fail(CommandNames.BrowserGetTab, rid, started, ErrorCodes.NotFound, $"Tab '{tabId}' not found.");
            }

            return Ok(CommandNames.BrowserGetTab, rid, started, tab, 1);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserGetTab, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> OpenTabAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            await EnsureConnectedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = await _cdp!.SendAsync(
                "Target.createTarget",
                new { url = string.IsNullOrWhiteSpace(url) ? "about:blank" : url },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var targetId = result.GetProperty("targetId").GetString()
                           ?? throw new InvalidOperationException("Missing targetId.");
            var tab = await AttachTabAsync(targetId, cancellationToken).ConfigureAwait(false);
            _activeTabId = tab.Id;
            return Ok(CommandNames.BrowserOpenTab, rid, started, tab, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserOpenTab, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> CloseTabAsync(string tabId, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            await EnsureConnectedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!_tabTargets.TryGetValue(tabId, out var targetId))
            {
                return Fail(CommandNames.BrowserCloseTab, rid, started, ErrorCodes.StaleTarget, "Unknown tab.");
            }

            await _cdp!.SendAsync("Target.closeTarget", new { targetId }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _tabTargets.TryRemove(tabId, out _);
            _tabSessions.TryRemove(tabId, out _);
            _handles.Remove(tabId);
            if (_activeTabId == tabId)
            {
                _activeTabId = _tabTargets.Keys.FirstOrDefault();
            }

            return Ok(CommandNames.BrowserCloseTab, rid, started, new { closed = true, tabId }, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserCloseTab, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> NavigateAsync(
        string url,
        string? tabId = null,
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Math.Clamp(timeoutMs, 1000, 120000));
            await _cdp!.SendAsync("Page.enable", sessionId: sessionId, cancellationToken: cts.Token)
                .ConfigureAwait(false);
            var nav = await _cdp.SendAsync("Page.navigate", new { url }, sessionId, cts.Token)
                .ConfigureAwait(false);
            await WaitForLoadAsync(sessionId, cts.Token).ConfigureAwait(false);
            var info = await EvaluateJsonAsync(sessionId, "({url: location.href, title: document.title})", cts.Token)
                .ConfigureAwait(false);
            return Ok(CommandNames.BrowserNavigate, rid, started, new
            {
                url = info.GetProperty("url").GetString(),
                title = info.GetProperty("title").GetString(),
                frameId = nav.TryGetProperty("frameId", out var f) ? f.GetString() : null
            }, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserNavigate, rid, started, MapCode(ex), ex.Message, retryable: true);
        }
    }

    public Task<ToolResult<object>> BackAsync(string? tabId = null, CancellationToken ct = default) =>
        HistoryAsync(CommandNames.BrowserBack, "window.history.back()", tabId, ct);

    public Task<ToolResult<object>> ForwardAsync(string? tabId = null, CancellationToken ct = default) =>
        HistoryAsync(CommandNames.BrowserForward, "window.history.forward()", tabId, ct);

    public async Task<ToolResult<object>> ReloadAsync(string? tabId = null, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            await _cdp!.SendAsync("Page.reload", new { ignoreCache = false }, sessionId, cancellationToken)
                .ConfigureAwait(false);
            await WaitForLoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return Ok(CommandNames.BrowserReload, rid, started, new { reloaded = true }, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserReload, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> QueryAsync(
        BrowserSelector selector,
        string? tabId = null,
        bool all = false,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var op = all ? CommandNames.BrowserQueryAll : CommandNames.BrowserQuery;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            var elements = await QueryElementsAsync(sessionId, selector, all, cancellationToken).ConfigureAwait(false);
            if (!all)
            {
                return elements.Count == 0
                    ? Fail(op, rid, started, ErrorCodes.NotFound, "No matching element.", retryable: true)
                    : Ok(op, rid, started, elements[0], elements.Count);
            }

            return Ok(op, rid, started, elements, elements.Count);
        }
        catch (Exception ex)
        {
            return Fail(op, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> ClickAsync(
        string? tabId,
        string? elementId,
        BrowserSelector? selector,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            var refId = await ResolveElementRefAsync(sessionId, elementId, selector, cancellationToken)
                .ConfigureAwait(false);
            await EvaluateAsync(sessionId, $"(() => {{ const el = document.querySelector('[data-sd-ref=\"{EscapeJs(refId)}\"]'); if (!el) throw new Error('missing'); el.click(); return true; }})()", cancellationToken)
                .ConfigureAwait(false);
            return Ok(CommandNames.BrowserClick, rid, started, new { clicked = true, elementId = refId }, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserClick, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> FillAsync(
        string value,
        string? tabId = null,
        string? elementId = null,
        BrowserSelector? selector = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            var refId = await ResolveElementRefAsync(sessionId, elementId, selector, cancellationToken)
                .ConfigureAwait(false);
            var valueJson = JsonSerializer.Serialize(value);
            var js =
                "(() => {\n" +
                $"  const el = document.querySelector('[data-sd-ref=\"{EscapeJs(refId)}\"]');\n" +
                "  if (!el) throw new Error('missing');\n" +
                "  el.focus();\n" +
                "  const proto = el.tagName.toLowerCase() === 'textarea'\n" +
                "    ? window.HTMLTextAreaElement.prototype\n" +
                "    : window.HTMLInputElement.prototype;\n" +
                "  const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;\n" +
                $"  if (setter) setter.call(el, {valueJson});\n" +
                $"  else el.value = {valueJson};\n" +
                "  el.dispatchEvent(new Event('input', { bubbles: true }));\n" +
                "  el.dispatchEvent(new Event('change', { bubbles: true }));\n" +
                "  return true;\n" +
                "})()";
            await EvaluateAsync(sessionId, js, cancellationToken).ConfigureAwait(false);
            return Ok(CommandNames.BrowserFill, rid, started, new { filled = true, elementId = refId }, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserFill, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> SelectAsync(
        string value,
        string? tabId = null,
        string? elementId = null,
        BrowserSelector? selector = null,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            var refId = await ResolveElementRefAsync(sessionId, elementId, selector, cancellationToken)
                .ConfigureAwait(false);
            var valueJson = JsonSerializer.Serialize(value);
            var labelJson = JsonSerializer.Serialize(label);
            var js =
                "(() => {\n" +
                $"  const el = document.querySelector('[data-sd-ref=\"{EscapeJs(refId)}\"]');\n" +
                "  if (!el || el.tagName.toLowerCase() !== 'select') throw new Error('not select');\n" +
                $"  const wantedValue = {valueJson};\n" +
                $"  const wantedLabel = {labelJson};\n" +
                "  let matched = false;\n" +
                "  for (const opt of Array.from(el.options)) {\n" +
                "    if ((wantedValue && opt.value === wantedValue) || (wantedLabel && (opt.textContent || '').trim() === wantedLabel) || (!wantedLabel && opt.value === wantedValue)) {\n" +
                "      el.value = opt.value;\n" +
                "      matched = true;\n" +
                "      break;\n" +
                "    }\n" +
                "  }\n" +
                "  if (!matched && wantedValue) { el.value = wantedValue; matched = true; }\n" +
                "  if (!matched) throw new Error('option not found');\n" +
                "  el.dispatchEvent(new Event('input', { bubbles: true }));\n" +
                "  el.dispatchEvent(new Event('change', { bubbles: true }));\n" +
                "  return el.value;\n" +
                "})()";
            var selected = await EvaluateAsync(sessionId, js, cancellationToken).ConfigureAwait(false);
            return Ok(CommandNames.BrowserSelect, rid, started, new { selected = true, value = selected, elementId = refId }, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserSelect, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> FocusAsync(
        string? tabId = null,
        string? elementId = null,
        BrowserSelector? selector = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            var refId = await ResolveElementRefAsync(sessionId, elementId, selector, cancellationToken)
                .ConfigureAwait(false);
            await EvaluateAsync(sessionId, $"(() => {{ const el = document.querySelector('[data-sd-ref=\"{EscapeJs(refId)}\"]'); if (!el) throw new Error('missing'); el.focus(); return true; }})()", cancellationToken)
                .ConfigureAwait(false);
            return Ok(CommandNames.BrowserFocus, rid, started, new { focused = true, elementId = refId }, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserFocus, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> GetTextAsync(
        string? tabId = null,
        string? elementId = null,
        BrowserSelector? selector = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            string text;
            if (elementId is not null || selector is not null)
            {
                var refId = await ResolveElementRefAsync(sessionId, elementId, selector, cancellationToken)
                    .ConfigureAwait(false);
                text = await EvaluateAsync(sessionId,
                    $"(() => {{ const el = document.querySelector('[data-sd-ref=\"{EscapeJs(refId)}\"]'); if (!el) throw new Error('missing'); return (el.innerText || el.textContent || '').trim(); }})()",
                    cancellationToken).ConfigureAwait(false) ?? "";
            }
            else
            {
                text = await EvaluateAsync(sessionId, "document.body ? (document.body.innerText || '') : ''", cancellationToken)
                    .ConfigureAwait(false) ?? "";
            }

            return Ok(CommandNames.BrowserGetText, rid, started, new { text }, 1);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserGetText, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> GetDomAsync(
        string? tabId = null,
        int maxChars = 50000,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            var html = await EvaluateAsync(sessionId, "document.documentElement ? document.documentElement.outerHTML : ''", cancellationToken)
                .ConfigureAwait(false) ?? "";
            if (html.Length > maxChars)
            {
                html = html[..maxChars];
            }

            return Ok(CommandNames.BrowserGetDom, rid, started, new { html, truncated = html.Length >= maxChars }, 1);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserGetDom, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> GetAccessibilityTreeAsync(
        string? tabId = null,
        int maxDepth = 8,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            await _cdp!.SendAsync("Accessibility.enable", sessionId: sessionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var result = await _cdp.SendAsync("Accessibility.getFullAXTree", sessionId: sessionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var nodes = result.TryGetProperty("nodes", out var n) ? n : default;
            var capped = CapAxDepth(nodes, maxDepth);
            return Ok(CommandNames.BrowserGetAccessibilityTree, rid, started, new { nodes = capped },
                capped is JsonElement je && je.ValueKind == JsonValueKind.Array ? je.GetArrayLength() : 0);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserGetAccessibilityTree, rid, started, MapCode(ex), ex.Message);
        }
    }

    public async Task<ToolResult<object>> WaitForAsync(
        BrowserSelector selector,
        string? tabId = null,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Math.Clamp(timeoutMs, 100, 120000));
            while (!cts.IsCancellationRequested)
            {
                var elements = await QueryElementsAsync(sessionId, selector, all: false, cts.Token).ConfigureAwait(false);
                if (elements.Count > 0)
                {
                    return Ok(CommandNames.BrowserWaitFor, rid, started, elements[0], 1);
                }

                await Task.Delay(100, cts.Token).ConfigureAwait(false);
            }

            return Fail(CommandNames.BrowserWaitFor, rid, started, ErrorCodes.Timeout, "Selector wait timed out.", retryable: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(CommandNames.BrowserWaitFor, rid, started, ErrorCodes.Timeout, "Selector wait timed out.", retryable: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserWaitFor, rid, started, MapCode(ex), ex.Message, retryable: true);
        }
    }

    public async Task<ToolResult<object>> WaitForNavigationAsync(
        string? tabId = null,
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Math.Clamp(timeoutMs, 100, 120000));
            await WaitForLoadAsync(sessionId, cts.Token).ConfigureAwait(false);
            return Ok(CommandNames.BrowserWaitForNavigation, rid, started, new { navigated = true }, 1);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserWaitForNavigation, rid, started, MapCode(ex), ex.Message, retryable: true);
        }
    }

    public async Task<ToolResult<object>> WaitForNetworkIdleAsync(
        string? tabId = null,
        int quietMs = 500,
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            await _cdp!.SendAsync("Network.enable", sessionId: sessionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Math.Clamp(timeoutMs, 100, 120000));
            var quiet = Math.Clamp(quietMs, 50, 10000);
            var lastBusy = DateTimeOffset.UtcNow;
            while (!cts.IsCancellationRequested)
            {
                if (Volatile.Read(ref _inFlight) == 0)
                {
                    if ((DateTimeOffset.UtcNow - lastBusy).TotalMilliseconds >= quiet)
                    {
                        return Ok(CommandNames.BrowserWaitForNetworkIdle, rid, started, new { idle = true }, 0);
                    }
                }
                else
                {
                    lastBusy = DateTimeOffset.UtcNow;
                }

                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }

            return Fail(CommandNames.BrowserWaitForNetworkIdle, rid, started, ErrorCodes.Timeout, "Network idle wait timed out.", retryable: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(CommandNames.BrowserWaitForNetworkIdle, rid, started, ErrorCodes.Timeout, "Network idle wait timed out.", retryable: true);
        }
        catch (Exception ex)
        {
            return Fail(CommandNames.BrowserWaitForNetworkIdle, rid, started, MapCode(ex), ex.Message, retryable: true);
        }
    }

    public ToolResult<object> GetDownloads()
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            return Ok(CommandNames.BrowserGetDownloads, rid, started, _downloads.ToList(), _downloads.Count);
        }
    }

    private async Task HistoryAsyncOp(string sessionId, string expression, CancellationToken ct)
    {
        await EvaluateAsync(sessionId, expression, ct).ConfigureAwait(false);
        await Task.Delay(200, ct).ConfigureAwait(false);
        await WaitForLoadAsync(sessionId, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult<object>> HistoryAsync(
        string operation,
        string expression,
        string? tabId,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            var sessionId = await ResolveSessionAsync(tabId, cancellationToken).ConfigureAwait(false);
            await HistoryAsyncOp(sessionId, expression, cancellationToken).ConfigureAwait(false);
            return Ok(operation, rid, started, new { ok = true }, 1, stateChanged: true);
        }
        catch (Exception ex)
        {
            return Fail(operation, rid, started, MapCode(ex), ex.Message);
        }
    }

    private async Task EnsureConnectedAsync(
        bool headless = false,
        string? executablePath = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BrowserService));
            }

            if (_cdp is { IsConnected: true })
            {
                return;
            }
        }

        var exe = executablePath ?? BrowserDiscovery.FindPreferredExecutable()
                   ?? throw new InvalidOperationException("No Chromium browser found on disk.");
        var port = BrowserDiscovery.FindFreeTcpPort();
        var userData = Path.Combine(Path.GetTempPath(), "sd-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userData);

        var args = new List<string>
        {
            $"--remote-debugging-port={port}",
            $"--user-data-dir={userData}",
            "--no-first-run",
            "--disable-default-apps",
            "--disable-popup-blocking",
            "--disable-background-networking",
            "about:blank"
        };
        if (headless)
        {
            args.Insert(0, "--headless=new");
            args.Insert(1, "--disable-gpu");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = false,
            CreateNoWindow = headless
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start browser.");
        string? wsUrl = null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancellationToken)
                    .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                wsUrl = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                if (!string.IsNullOrWhiteSpace(wsUrl))
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }

        if (string.IsNullOrWhiteSpace(wsUrl))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new InvalidOperationException("Timed out waiting for CDP endpoint.");
        }

        var cdp = new CdpConnection();
        cdp.EventReceived += OnCdpEvent;
        await cdp.ConnectAsync(wsUrl, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _chromeProcess = process;
            _userDataDir = userData;
            _debugPort = port;
            _cdp = cdp;
            _browserId = _handles.Allocate(
                HandleKind.Browser,
                exe,
                new Dictionary<string, object?>
                {
                    ["name"] = Path.GetFileNameWithoutExtension(exe),
                    ["path"] = exe,
                    ["debugPort"] = port,
                    ["running"] = true,
                    ["managed"] = true
                });
        }

        try
        {
            await cdp.SendAsync("Target.setDiscoverTargets", new { discover = true }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Optional.
        }

        await EnableDownloadTrackingAsync(cancellationToken).ConfigureAwait(false);

        // Attach existing pages.
        var targets = await cdp.SendAsync("Target.getTargets", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (targets.ValueKind == JsonValueKind.Object && targets.TryGetProperty("targetInfos", out var infos))
        {
            foreach (var info in infos.EnumerateArray())
            {
                if (info.TryGetProperty("type", out var type) && type.GetString() == "page")
                {
                    var targetId = info.GetProperty("targetId").GetString();
                    if (targetId is not null)
                    {
                        var tab = await AttachTabAsync(targetId, cancellationToken).ConfigureAwait(false);
                        _activeTabId ??= tab.Id;
                    }
                }
            }
        }

        if (_activeTabId is null)
        {
            var created = await cdp.SendAsync("Target.createTarget", new { url = "about:blank" }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var targetId = created.GetProperty("targetId").GetString()!;
            var tab = await AttachTabAsync(targetId, cancellationToken).ConfigureAwait(false);
            _activeTabId = tab.Id;
        }
    }

    private async Task EnableDownloadTrackingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var downloadPath = Path.Combine(_userDataDir ?? Path.GetTempPath(), "downloads");
            Directory.CreateDirectory(downloadPath);
            await _cdp!.SendAsync("Browser.setDownloadBehavior", new
            {
                behavior = "allow",
                downloadPath,
                eventsEnabled = true
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Download domain may be unavailable; get_downloads still returns tracked list.
        }
    }

    private void OnCdpEvent(string method, JsonElement? paramsEl, string? sessionId)
    {
        if (method is "Network.requestWillBeSent")
        {
            Interlocked.Increment(ref _inFlight);
        }
        else if (method is "Network.loadingFinished" or "Network.loadingFailed")
        {
            Interlocked.Decrement(ref _inFlight);
            if (_inFlight < 0)
            {
                Volatile.Write(ref _inFlight, 0);
            }
        }
        else if (method == "Browser.downloadWillBegin" && paramsEl is { } p)
        {
            lock (_gate)
            {
                _downloads.Add(new BrowserDownloadInfo
                {
                    Guid = p.TryGetProperty("guid", out var g) ? g.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                    Url = p.TryGetProperty("url", out var u) ? u.GetString() : null,
                    SuggestedFilename = p.TryGetProperty("suggestedFilename", out var f) ? f.GetString() : null,
                    State = "inProgress"
                });
            }
        }
        else if (method == "Browser.downloadProgress" && paramsEl is { } prog)
        {
            lock (_gate)
            {
                var guid = prog.TryGetProperty("guid", out var g) ? g.GetString() : null;
                var existing = _downloads.LastOrDefault(d => d.Guid == guid);
                if (existing is not null)
                {
                    _downloads.Remove(existing);
                    _downloads.Add(new BrowserDownloadInfo
                    {
                        Guid = existing.Guid,
                        Url = existing.Url,
                        SuggestedFilename = existing.SuggestedFilename,
                        State = prog.TryGetProperty("state", out var s) ? s.GetString() : existing.State,
                        ReceivedBytes = prog.TryGetProperty("receivedBytes", out var rb) && rb.TryGetInt64(out var rbv) ? rbv : existing.ReceivedBytes,
                        TotalBytes = prog.TryGetProperty("totalBytes", out var tb) && tb.TryGetInt64(out var tbv) ? tbv : existing.TotalBytes
                    });
                }
            }
        }
    }

    private async Task<BrowserTabInfo> AttachTabAsync(string targetId, CancellationToken cancellationToken)
    {
        var attach = await _cdp!.SendAsync(
            "Target.attachToTarget",
            new { targetId, flatten = true },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var sessionId = attach.GetProperty("sessionId").GetString()
                        ?? throw new InvalidOperationException("Missing sessionId.");

        await _cdp.SendAsync("Page.enable", sessionId: sessionId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _cdp.SendAsync("Runtime.enable", sessionId: sessionId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        string? url = null;
        string? title = null;
        try
        {
            var info = await EvaluateJsonAsync(sessionId, "({url: location.href, title: document.title})", cancellationToken)
                .ConfigureAwait(false);
            url = info.GetProperty("url").GetString();
            title = info.GetProperty("title").GetString();
        }
        catch
        {
            // Page may still be loading.
        }

        var tabId = _handles.Allocate(
            HandleKind.Tab,
            targetId,
            new Dictionary<string, object?>
            {
                ["targetId"] = targetId,
                ["sessionId"] = sessionId,
                ["browserId"] = _browserId,
                ["url"] = url,
                ["title"] = title
            });
        _tabSessions[tabId] = sessionId;
        _tabTargets[tabId] = targetId;
        return new BrowserTabInfo
        {
            Id = tabId,
            BrowserId = _browserId,
            TargetId = targetId,
            Url = url,
            Title = title,
            Active = _activeTabId is null || _activeTabId == tabId
        };
    }

    private async Task<IReadOnlyList<BrowserTabInfo>> ListTabsInternalAsync(CancellationToken cancellationToken)
    {
        var result = await _cdp!.SendAsync("Target.getTargets", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var tabs = new List<BrowserTabInfo>();
        if (!result.TryGetProperty("targetInfos", out var infos))
        {
            return tabs;
        }

        foreach (var info in infos.EnumerateArray())
        {
            if (!(info.TryGetProperty("type", out var type) && type.GetString() == "page"))
            {
                continue;
            }

            var targetId = info.GetProperty("targetId").GetString()!;
            var existing = _tabTargets.FirstOrDefault(kv => kv.Value == targetId).Key;
            string tabId;
            if (existing is not null)
            {
                tabId = existing;
            }
            else
            {
                var attached = await AttachTabAsync(targetId, cancellationToken).ConfigureAwait(false);
                tabId = attached.Id;
            }

            tabs.Add(new BrowserTabInfo
            {
                Id = tabId,
                BrowserId = _browserId,
                TargetId = targetId,
                Url = info.TryGetProperty("url", out var u) ? u.GetString() : null,
                Title = info.TryGetProperty("title", out var t) ? t.GetString() : null,
                Active = tabId == _activeTabId
            });
        }

        return tabs;
    }

    private async Task<string> ResolveSessionAsync(string? tabId, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var id = tabId ?? _activeTabId;
        if (id is null || !_tabSessions.TryGetValue(id, out var sessionId))
        {
            var tabs = await ListTabsInternalAsync(cancellationToken).ConfigureAwait(false);
            var tab = tabs.FirstOrDefault();
            if (tab is null)
            {
                throw new InvalidOperationException("No browser tabs available.");
            }

            _activeTabId = tab.Id;
            return _tabSessions[tab.Id];
        }

        _activeTabId = id;
        return sessionId;
    }

    private async Task<List<DomElementInfo>> QueryElementsAsync(
        string sessionId,
        BrowserSelector selector,
        bool all,
        CancellationToken cancellationToken)
    {
        var selJson = JsonSerializer.Serialize(selector, JsonDefaults.Options);
        var expr = $@"(() => {{
{SelectorHelperJs}
  const sel = {selJson};
  const nodes = __sdQuery(sel, {(all ? "true" : "false")});
  const list = {(all ? "nodes" : "nodes ? [nodes] : []")};
  return list.map((el, i) => {{
    const raw = (el.tagName + '|' + (__sdAccessibleName(el)||'') + '|' + i + '|' + (el.getAttribute('data-testid')||'')).toLowerCase();
    let h = 0;
    for (let i2 = 0; i2 < raw.length; i2++) h = ((h << 5) - h + raw.charCodeAt(i2)) | 0;
    const id = 'dom_' + (h >>> 0).toString(16);
    return __sdDescribe(el, id);
  }});
}})()";
        var json = await EvaluateJsonAsync(sessionId, expr, cancellationToken).ConfigureAwait(false);
        var list = new List<DomElementInfo>();
        if (json.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in json.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var id = item.GetProperty("id").GetString()!;
            _handles.Allocate(
                HandleKind.Element,
                id,
                new Dictionary<string, object?>
                {
                    ["tag"] = item.TryGetProperty("tag", out var tag) ? tag.GetString() : null,
                    ["sessionHint"] = sessionId
                },
                preferredId: id);

            list.Add(new DomElementInfo
            {
                Id = id,
                Tag = item.TryGetProperty("tag", out var t) ? t.GetString() : null,
                Role = item.TryGetProperty("role", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetString() : null,
                Name = item.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() : null,
                Text = item.TryGetProperty("text", out var tx) && tx.ValueKind != JsonValueKind.Null ? tx.GetString() : null,
                Value = item.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null
            });
        }

        return list;
    }

    private async Task<string> ResolveElementRefAsync(
        string sessionId,
        string? elementId,
        BrowserSelector? selector,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(elementId))
        {
            return elementId;
        }

        if (selector is null)
        {
            throw new ArgumentException("elementId or selector is required.");
        }

        var elements = await QueryElementsAsync(sessionId, selector, all: false, cancellationToken)
            .ConfigureAwait(false);
        if (elements.Count == 0)
        {
            throw new InvalidOperationException("No matching element.");
        }

        return elements[0].Id;
    }

    private async Task WaitForLoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string method, JsonElement? _, string? sid)
        {
            if (method == "Page.loadEventFired" && (sid is null || sid == sessionId))
            {
                tcs.TrySetResult();
            }
        }

        _cdp!.EventReceived += Handler;
        try
        {
            // Also accept already-loaded pages.
            var ready = await EvaluateAsync(sessionId, "document.readyState", cancellationToken).ConfigureAwait(false);
            if (string.Equals(ready, "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cdp.EventReceived -= Handler;
        }
    }

    private async Task<string?> EvaluateAsync(string sessionId, string expression, CancellationToken cancellationToken)
    {
        var result = await _cdp!.SendAsync(
            "Runtime.evaluate",
            new
            {
                expression,
                returnByValue = true,
                awaitPromise = true
            },
            sessionId,
            cancellationToken).ConfigureAwait(false);

        if (result.TryGetProperty("exceptionDetails", out var ex))
        {
            var msg = ex.TryGetProperty("text", out var t) ? t.GetString() : "Runtime exception";
            throw new InvalidOperationException(msg);
        }

        if (!result.TryGetProperty("result", out var remote))
        {
            return null;
        }

        if (remote.TryGetProperty("value", out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => value.GetRawText()
            };
        }

        return null;
    }

    private async Task<JsonElement> EvaluateJsonAsync(string sessionId, string expression, CancellationToken cancellationToken)
    {
        var result = await _cdp!.SendAsync(
            "Runtime.evaluate",
            new
            {
                expression,
                returnByValue = true,
                awaitPromise = true
            },
            sessionId,
            cancellationToken).ConfigureAwait(false);

        if (result.TryGetProperty("exceptionDetails", out var ex))
        {
            var msg = ex.TryGetProperty("text", out var t) ? t.GetString() : "Runtime exception";
            throw new InvalidOperationException(msg);
        }

        if (result.TryGetProperty("result", out var remote) && remote.TryGetProperty("value", out var value))
        {
            return value.Clone();
        }

        return default;
    }

    private static object CapAxDepth(JsonElement nodes, int maxDepth)
    {
        if (nodes.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<object>();
        }

        // CDP AX tree is flat with parentId/childIds; return truncated list by depth walk.
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("nodeId", out var idEl))
            {
                var id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString()! : idEl.ToString();
                byId[id] = node.Clone();
            }
        }

        var roots = nodes.EnumerateArray()
            .Where(n => !n.TryGetProperty("parentId", out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            .ToList();
        if (roots.Count == 0 && nodes.GetArrayLength() > 0)
        {
            roots.Add(nodes[0].Clone());
        }

        var kept = new List<JsonElement>();
        void Walk(JsonElement node, int depth)
        {
            if (depth > maxDepth || kept.Count > 500)
            {
                return;
            }

            kept.Add(node.Clone());
            if (!node.TryGetProperty("childIds", out var children) || children.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var childId in children.EnumerateArray())
            {
                var id = childId.ValueKind == JsonValueKind.String ? childId.GetString()! : childId.ToString();
                if (byId.TryGetValue(id, out var child))
                {
                    Walk(child, depth + 1);
                }
            }
        }

        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        return kept.Select(e => JsonSerializer.Deserialize<object>(e.GetRawText(), JsonDefaults.Options)).ToList()!;
    }

    private static string StableBrowserId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return "brw_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string EscapeJs(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string MapCode(Exception ex) =>
        ex switch
        {
            TimeoutException => ErrorCodes.Timeout,
            OperationCanceledException => ErrorCodes.Timeout,
            ArgumentException => ErrorCodes.InvalidArgument,
            InvalidOperationException when ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("No matching", StringComparison.OrdinalIgnoreCase)
                => ErrorCodes.NotFound,
            InvalidOperationException when ex.Message.Contains("Unknown tab", StringComparison.OrdinalIgnoreCase)
                => ErrorCodes.StaleTarget,
            _ => ErrorCodes.Internal
        };

    private static ToolResult<object> Ok(
        string operation,
        string requestId,
        DateTimeOffset started,
        object data,
        int elements,
        bool? stateChanged = null) =>
        ToolResult<object>.Success(
            data,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = operation,
                DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                ElementsInspected = elements,
                CacheHit = false,
                Provider = "CDP"
            },
            stateChanged);

    private static ToolResult<object> Fail(
        string operation,
        string requestId,
        DateTimeOffset started,
        string code,
        string message,
        bool retryable = false) =>
        ToolResult<object>.Failure(
            new ErrorInfo { Code = code, Message = message, Retryable = retryable },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = operation,
                DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                ElementsInspected = 0,
                CacheHit = false,
                Provider = "CDP"
            });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cdp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_chromeProcess is { HasExited: false })
            {
                _chromeProcess.Kill(entireProcessTree: true);
                _chromeProcess.WaitForExit(5000);
            }
        }
        catch
        {
            // ignore
        }

        _chromeProcess?.Dispose();

        if (!string.IsNullOrWhiteSpace(_userDataDir))
        {
            try
            {
                Directory.Delete(_userDataDir, recursive: true);
            }
            catch
            {
                // Profile may still be locked briefly.
            }
        }
    }
}
