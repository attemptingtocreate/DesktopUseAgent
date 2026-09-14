using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Execution.Conditions;
using SemanticDesktop.UIA.Automation;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Agent;

public sealed class ConditionProbe : IConditionProbe
{
    private readonly WindowService _windows;
    private readonly UIAutomationService _uia;

    public ConditionProbe(WindowService windows, UIAutomationService uia)
    {
        _windows = windows;
        _uia = uia;
    }

    public async Task<bool> WindowExistsAsync(
        string? process,
        string? titleContains,
        string? windowId,
        CancellationToken cancellationToken)
    {
        var windows = await _windows.ListAsync(cancellationToken).ConfigureAwait(false);
        return windows.Any(w =>
            (windowId is null || string.Equals(w.Id, windowId, StringComparison.Ordinal)) &&
            (process is null || w.Process.Contains(process, StringComparison.OrdinalIgnoreCase)) &&
            (titleContains is null || w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<bool> ProcessRunningAsync(string processName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        var running = Process.GetProcessesByName(name).Length > 0;
        return Task.FromResult(running);
    }

    public async Task<bool> UiExistsAsync(
        string? windowId,
        Dictionary<string, JsonElement>? selector,
        CancellationToken cancellationToken)
    {
        var found = await FindAsync(windowId, selector, cancellationToken).ConfigureAwait(false);
        return found.Count > 0;
    }

    public async Task<bool> UiEnabledAsync(
        string? windowId,
        Dictionary<string, JsonElement>? selector,
        CancellationToken cancellationToken)
    {
        var found = await FindAsync(windowId, selector, cancellationToken).ConfigureAwait(false);
        return found.Any(e => e.Enabled);
    }

    public async Task<bool> UiValueEqualsAsync(
        string? windowId,
        Dictionary<string, JsonElement>? selector,
        string expected,
        CancellationToken cancellationToken)
    {
        var found = await FindAsync(windowId, selector, cancellationToken).ConfigureAwait(false);
        if (found.Count == 0)
        {
            return false;
        }

        var text = await _uia.GetTextAsync(new Core.Targets.ElementHandle { Id = found[0].Id }, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(text, expected, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<UIElement>> FindAsync(
        string? windowId,
        Dictionary<string, JsonElement>? selector,
        CancellationToken cancellationToken)
    {
        var query = new UIFindQuery
        {
            WindowId = windowId,
            MaxResults = 5,
            Selector = new UIFindSelector
            {
                Name = GetString(selector, "name"),
                NameContains = GetString(selector, "nameContains"),
                AutomationId = GetString(selector, "automationId"),
                ClassName = GetString(selector, "className"),
                ControlType = GetString(selector, "controlType") ?? GetString(selector, "type"),
                FrameworkId = GetString(selector, "frameworkId")
            }
        };
        return await _uia.FindAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private static string? GetString(Dictionary<string, JsonElement>? selector, string name)
    {
        if (selector is null || !selector.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }
}
