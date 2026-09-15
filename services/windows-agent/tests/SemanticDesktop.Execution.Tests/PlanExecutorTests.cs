using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Plans;
using SemanticDesktop.Execution.Conditions;
using SemanticDesktop.Execution.Plans;
using SemanticDesktop.Execution.References;
using SemanticDesktop.Files;

namespace SemanticDesktop.Execution.Tests;

public class StepReferenceResolverTests
{
    [Fact]
    public void TargetFrom_ResolvesElementId()
    {
        var findOutput = JsonSerializer.SerializeToElement(new
        {
            ok = true,
            data = new
            {
                elements = new[] { new { id = "uia_abc" } }
            }
        });
        var args = new Dictionary<string, JsonElement>
        {
            ["targetFrom"] = JsonSerializer.SerializeToElement("find-editor"),
            ["value"] = JsonSerializer.SerializeToElement("hi")
        };
        var resolved = StepReferenceResolver.ResolveArgs(args, new Dictionary<string, JsonElement>
        {
            ["find-editor"] = findOutput
        });
        Assert.Equal("uia_abc", resolved.GetProperty("elementId").GetString());
        Assert.Equal("hi", resolved.GetProperty("value").GetString());
    }

    [Fact]
    public void JsonPath_ResolvesNestedValue()
    {
        var output = JsonSerializer.SerializeToElement(new
        {
            data = new { elements = new[] { new { id = "uia_1" }, new { id = "uia_2" } } }
        });
        var value = StepReferenceResolver.ResolvePath(
            "$steps.find.data.elements[1].id",
            new Dictionary<string, JsonElement> { ["find"] = output });
        Assert.Equal("uia_2", value.GetString());
    }
}

public class PlanExecutorUnitTests
{
    [Fact]
    public async Task Execute_Retries_ThenSucceeds()
    {
        var attempts = 0;
        var files = new FileService();
        var conditions = new ConditionEvaluator(new FakeProbe(), files);
        var executor = new PlanExecutor(conditions, (_, _, _) =>
        {
            attempts++;
            if (attempts < 2)
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    ok = false,
                    error = new { code = "TIMEOUT", message = "transient", retryable = true }
                }));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, data = new { done = true } }));
        });

        var state = await executor.ExecuteAsync(new ExecutionPlan
        {
            Name = "retry-demo",
            Steps =
            {
                new PlanStep
                {
                    Id = "s1",
                    Action = "test.action",
                    Retries = new RetryOptions { Count = 2, DelayMs = 10 }
                }
            }
        }, CancellationToken.None);

        Assert.Equal(PlanStatus.Succeeded, state.Status);
        Assert.Equal(2, state.Steps[0].Attempts);
    }

    [Fact]
    public async Task Execute_WhenFalse_SkipsStep()
    {
        var files = new FileService();
        var conditions = new ConditionEvaluator(new FakeProbe { WindowExists = false }, files);
        var executor = new PlanExecutor(conditions, (_, _, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true })));

        var state = await executor.ExecuteAsync(new ExecutionPlan
        {
            Steps =
            {
                new PlanStep
                {
                    Id = "optional",
                    Action = "test.action",
                    When = new Condition { Type = "window.exists", Process = "missing.exe" }
                }
            }
        }, CancellationToken.None);

        Assert.Equal(PlanStatus.Succeeded, state.Status);
        Assert.Equal(PlanStatus.Skipped, state.Steps[0].Status);
    }

    [Fact]
    public async Task Cancel_StopsRunningPlan()
    {
        var files = new FileService();
        var conditions = new ConditionEvaluator(new FakeProbe(), files);
        var started = new TaskCompletionSource();
        var executor = new PlanExecutor(conditions, async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(10_000, ct);
            return JsonSerializer.SerializeToElement(new { ok = true });
        });

        var run = executor.ExecuteAsync(new ExecutionPlan
        {
            Id = "plan_cancel_test",
            Steps = { new PlanStep { Id = "slow", Action = "test.action", TimeoutMs = 20_000 } }
        }, CancellationToken.None);

        await started.Task;
        Assert.True(executor.Cancel("plan_cancel_test"));
        var state = await run;
        Assert.Equal(PlanStatus.Cancelled, state.Status);
    }

    [Fact]
    public async Task Execute_FusesFilesystemExists_AndRunsParallelReads()
    {
        var files = new FileService();
        var conditions = new ConditionEvaluator(new FakeProbe(), files);
        var running = 0;
        var max = 0;
        var actions = new List<string>();
        var executor = new PlanExecutor(conditions, async (action, args, _) =>
        {
            lock (actions) { actions.Add(action); }
            if (action == CommandNames.FilesystemExists || action == CommandNames.FilesystemInspect)
            {
                var current = Interlocked.Increment(ref running);
                while (true)
                {
                    var snapshot = Volatile.Read(ref max);
                    if (current <= snapshot || Interlocked.CompareExchange(ref max, current, snapshot) == snapshot)
                    {
                        break;
                    }
                }

                await Task.Delay(40);
                Interlocked.Decrement(ref running);
            }

            return JsonSerializer.SerializeToElement(new { ok = true, data = new { exists = true } });
        });

        var dir = Path.GetTempPath();
        var state = await executor.ExecuteAsync(new ExecutionPlan
        {
            Steps =
            {
                new PlanStep { Id = "a", Action = CommandNames.FilesystemExists, Args = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(Path.Combine(dir, "a")) } },
                new PlanStep { Id = "b", Action = CommandNames.FilesystemExists, Args = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(Path.Combine(dir, "b")) } }
            }
        }, CancellationToken.None);

        Assert.Equal(PlanStatus.Succeeded, state.Status);
        Assert.True(state.FusedCount >= 1 || state.ParallelGroupCount >= 1);
        Assert.Contains(actions, a => a is CommandNames.FilesystemInspect or CommandNames.FilesystemExists);
    }

    private sealed class FakeProbe : IConditionProbe
    {
        public bool WindowExists { get; set; } = true;

        public Task<bool> WindowExistsAsync(string? process, string? titleContains, string? windowId, CancellationToken cancellationToken)
            => Task.FromResult(WindowExists);

        public Task<bool> ProcessRunningAsync(string processName, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> UiExistsAsync(string? windowId, Dictionary<string, JsonElement>? selector, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> UiEnabledAsync(string? windowId, Dictionary<string, JsonElement>? selector, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> UiValueEqualsAsync(string? windowId, Dictionary<string, JsonElement>? selector, string expected, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
