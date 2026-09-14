using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Plans;
using SemanticDesktop.Files;

namespace SemanticDesktop.Execution.Conditions;

public interface IConditionProbe
{
    Task<bool> WindowExistsAsync(string? process, string? titleContains, string? windowId, CancellationToken cancellationToken);
    Task<bool> ProcessRunningAsync(string processName, CancellationToken cancellationToken);
    Task<bool> UiExistsAsync(string? windowId, Dictionary<string, JsonElement>? selector, CancellationToken cancellationToken);
    Task<bool> UiEnabledAsync(string? windowId, Dictionary<string, JsonElement>? selector, CancellationToken cancellationToken);
    Task<bool> UiValueEqualsAsync(string? windowId, Dictionary<string, JsonElement>? selector, string expected, CancellationToken cancellationToken);
}

public sealed class ConditionEvaluator
{
    private readonly IConditionProbe _probe;
    private readonly FileService _files;

    public ConditionEvaluator(IConditionProbe probe, FileService files)
    {
        _probe = probe;
        _files = files;
    }

    public async Task WaitAsync(Condition condition, int defaultTimeoutMs, CancellationToken cancellationToken)
    {
        if (string.Equals(condition.Type, "time.delay", StringComparison.OrdinalIgnoreCase))
        {
            var delay = condition.DelayMs ?? 0;
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var timeoutMs = condition.TimeoutMs ?? defaultTimeoutMs;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds <= timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await EvaluateOnceAsync(condition, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"{ErrorCodes.ConditionTimeout}: condition '{condition.Type}' not satisfied within {timeoutMs}ms.");
    }

    public Task<bool> EvaluateOnceAsync(Condition condition, CancellationToken cancellationToken) =>
        condition.Type.ToLowerInvariant() switch
        {
            "window.exists" => _probe.WindowExistsAsync(condition.Process, condition.TitleContains, condition.WindowId, cancellationToken),
            "window.not_exists" => Negate(_probe.WindowExistsAsync(condition.Process, condition.TitleContains, condition.WindowId, cancellationToken)),
            "process.running" => _probe.ProcessRunningAsync(Require(condition.Process, "process"), cancellationToken),
            "process.exited" => Negate(_probe.ProcessRunningAsync(Require(condition.Process, "process"), cancellationToken)),
            "ui.exists" => _probe.UiExistsAsync(condition.WindowId, condition.Selector ?? BuildSelector(condition), cancellationToken),
            "ui.enabled" => _probe.UiEnabledAsync(condition.WindowId, condition.Selector ?? BuildSelector(condition), cancellationToken),
            "ui.value_equals" => _probe.UiValueEqualsAsync(
                condition.WindowId,
                condition.Selector ?? BuildSelector(condition),
                Require(condition.Value, "value"),
                cancellationToken),
            "file.exists" => Task.FromResult(_files.Exists(Require(condition.Path, "path"))),
            "file.not_exists" => Task.FromResult(!_files.Exists(Require(condition.Path, "path"))),
            "time.delay" => Task.FromResult(true),
            // Event-style aliases map to local polling conditions for Phase 2.
            "window.opened" => _probe.WindowExistsAsync(condition.Process, condition.TitleContains, condition.WindowId, cancellationToken),
            "window.closed" => Negate(_probe.WindowExistsAsync(condition.Process, condition.TitleContains, condition.WindowId, cancellationToken)),
            _ => throw new ArgumentException($"{ErrorCodes.Unsupported}: condition '{condition.Type}'.")
        };

    private static async Task<bool> Negate(Task<bool> task) => !await task.ConfigureAwait(false);

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{ErrorCodes.InvalidArgument}: '{name}' is required.")
            : value;

    private static Dictionary<string, JsonElement>? BuildSelector(Condition condition)
    {
        var selector = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(condition.Name))
        {
            selector["name"] = JsonSerializer.SerializeToElement(condition.Name);
        }

        if (!string.IsNullOrWhiteSpace(condition.AutomationId))
        {
            selector["automationId"] = JsonSerializer.SerializeToElement(condition.AutomationId);
        }

        if (!string.IsNullOrWhiteSpace(condition.ControlType))
        {
            selector["controlType"] = JsonSerializer.SerializeToElement(condition.ControlType);
        }

        return selector.Count == 0 ? null : selector;
    }
}
