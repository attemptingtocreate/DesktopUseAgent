using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Commands;

namespace SemanticDesktop.Cli;

public sealed class BenchmarkRunner
{
    private readonly IBenchmarkAgentClient _client;

    public BenchmarkRunner(IBenchmarkAgentClient client) => _client = client;

    public BenchmarkSummary CreateDryRunPlan(BenchmarkOptions options) =>
        new()
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Mode = "dry-run",
            Parameters = ToParameters(options),
            Scenarios = BuildScenarioPlan(options)
        };

    public async Task<BenchmarkSummary> RunAsync(BenchmarkOptions options, CancellationToken cancellationToken)
    {
        var scenarios = new List<BenchmarkScenarioResult>();
        scenarios.Add(await RunRepeatedAsync("ping", CommandNames.SystemPing, null, options, cancellationToken)
            .ConfigureAwait(false));
        scenarios.Add(await RunRepeatedAsync("capabilities", CommandNames.DesktopGetCapabilities, null, options, cancellationToken)
            .ConfigureAwait(false));
        scenarios.Add(await RunRepeatedAsync("window-list", CommandNames.WindowList, null, options, cancellationToken)
            .ConfigureAwait(false));
        scenarios.Add(await RunRepeatedAsync("graph", CommandNames.DesktopGetGraph, new { forceRefresh = true }, options, cancellationToken)
            .ConfigureAwait(false));

        if (options.Live)
        {
            scenarios.Add(await RunOpenUrlAsync(options, cancellationToken).ConfigureAwait(false));
            scenarios.Add(await RunWindowMoveAsync(options, cancellationToken).ConfigureAwait(false));
            scenarios.Add(await RunRobloxHierarchyAsync(options, cancellationToken).ConfigureAwait(false));
            scenarios.Add(await RunBlenderComparisonAsync(options, cancellationToken).ConfigureAwait(false));
        }

        return new BenchmarkSummary
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Mode = options.Live ? "live" : "safe",
            Parameters = ToParameters(options),
            Scenarios = scenarios
        };
    }

    private static BenchmarkParameters ToParameters(BenchmarkOptions options) =>
        new()
        {
            Iterations = Math.Max(1, options.Iterations),
            Monitor = options.Monitor,
            Url = options.Url
        };

    private static IReadOnlyList<BenchmarkScenarioResult> BuildScenarioPlan(BenchmarkOptions options)
    {
        var iterations = Math.Max(1, options.Iterations);
        var list = new List<BenchmarkScenarioResult>
        {
            Plan("ping", CommandNames.SystemPing, iterations),
            Plan("capabilities", CommandNames.DesktopGetCapabilities, iterations),
            Plan("window-list", CommandNames.WindowList, iterations),
            Plan("graph", CommandNames.DesktopGetGraph, iterations)
        };

        if (options.Live)
        {
            list.Add(Plan("open-url", CommandNames.BrowserOpenTab, iterations, "Opens controlled URL and closes owned tab"));
            list.Add(Plan("window-move-monitor", CommandNames.WindowMove, 1, "Launches Notepad, moves owned window, cleans up process"));
            list.Add(Plan("studio-hierarchy", CommandNames.RobloxGetHierarchy, 1, "Skipped unless Roblox plugin connected"));
            list.Add(Plan("blender-batch-vs-individual", CommandNames.BlenderBatch, 1, "Skipped unless Blender executable available"));
        }

        return list;

        static BenchmarkScenarioResult Plan(string name, string method, int iterationCount, string? note = null) =>
            new()
            {
                Name = name,
                Method = method,
                Status = "planned",
                SkipReason = note,
                Iterations = iterationCount
            };
    }

    private async Task<BenchmarkScenarioResult> RunRepeatedAsync(
        string name,
        string method,
        object? parameters,
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var iterations = Math.Max(1, options.Iterations);
        var wall = new List<long>(iterations);
        var tool = new List<long>(iterations);
        var ok = true;

        for (var i = 0; i < iterations; i++)
        {
            var result = await _client.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            wall.Add(result.WallMs);
            if (result.ToolDurationMs is long toolMs)
            {
                tool.Add(toolMs);
            }

            ok &= result.Ok;
        }

        return BuildResult(name, method, "completed", null, ok, iterations, wall, tool);
    }

    private async Task<BenchmarkScenarioResult> RunOpenUrlAsync(BenchmarkOptions options, CancellationToken cancellationToken)
    {
        const string name = "open-url";
        var iterations = Math.Max(1, options.Iterations);
        var wall = new List<long>();
        var tool = new List<long>();
        string? tabId = null;

        try
        {
            for (var i = 0; i < iterations; i++)
            {
                var open = await _client.CallAsync(
                    CommandNames.BrowserOpenTab,
                    new { url = options.Url },
                    cancellationToken).ConfigureAwait(false);
                wall.Add(open.WallMs);
                if (open.ToolDurationMs is long openToolMs)
                {
                    tool.Add(openToolMs);
                }

                if (!open.Ok)
                {
                    if (IsUnavailable(open, "browser", "cdp", "chrome", "edge"))
                    {
                        return Skipped(name, CommandNames.BrowserOpenTab, open.ErrorMessage ?? "Browser/CDP not available");
                    }

                    return BuildResult(
                        name,
                        CommandNames.BrowserOpenTab,
                        "failed",
                        open.ErrorMessage ?? open.ErrorCode ?? "browser.open_tab failed",
                        false,
                        iterations,
                        wall,
                        tool);
                }

                tabId ??= ExtractString(open.Raw, "data", "id");
            }

            return BuildResult(name, CommandNames.BrowserOpenTab, "completed", null, true, iterations, wall, tool);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tabId))
            {
                _ = await _client.CallAsync(CommandNames.BrowserCloseTab, new { tabId }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<BenchmarkScenarioResult> RunWindowMoveAsync(BenchmarkOptions options, CancellationToken cancellationToken)
    {
        const string name = "window-move-monitor";
        Process? notepad = null;
        try
        {
            var launch = await _client.CallAsync(
                CommandNames.ProcessLaunch,
                new { executable = "notepad.exe" },
                cancellationToken).ConfigureAwait(false);
            if (!launch.Ok)
            {
                return Skipped(name, CommandNames.WindowMove, launch.ErrorMessage ?? "Failed to launch notepad.exe");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            notepad = Process.GetProcessesByName("notepad").OrderByDescending(p => p.StartTime).FirstOrDefault();
            if (notepad is null)
            {
                return Skipped(name, CommandNames.WindowMove, "Launched Notepad process not found for cleanup tracking.");
            }

            var wait = await _client.CallAsync(
                CommandNames.WindowWaitFor,
                new { process = "notepad.exe", timeoutMs = 10000 },
                cancellationToken).ConfigureAwait(false);
            if (!wait.Ok)
            {
                return BuildResult(name, CommandNames.WindowMove, "failed", wait.ErrorMessage, false, 1,
                    [wait.WallMs], wait.ToolDurationMs is long w ? [w] : []);
            }

            var windowId = ExtractString(wait.Raw, "data", "window", "id");
            if (windowId is null)
            {
                return BuildResult(name, CommandNames.WindowMove, "failed", "window.wait_for returned no window id", false, 1,
                    [wait.WallMs], wait.ToolDurationMs is long w2 ? [w2] : []);
            }

            var move = await _client.CallAsync(
                CommandNames.WindowMove,
                new { windowId, monitor = options.Monitor },
                cancellationToken).ConfigureAwait(false);

            var wall = new List<long> { wait.WallMs, move.WallMs };
            var tool = new List<long>();
            if (wait.ToolDurationMs is long waitTool)
            {
                tool.Add(waitTool);
            }

            if (move.ToolDurationMs is long moveTool)
            {
                tool.Add(moveTool);
            }

            return BuildResult(
                name,
                CommandNames.WindowMove,
                move.Ok ? "completed" : "failed",
                move.ErrorMessage,
                move.Ok,
                1,
                wall,
                tool);
        }
        catch (Exception ex)
        {
            return BuildResult(name, CommandNames.WindowMove, "failed", ex.Message, false, 1, [], []);
        }
        finally
        {
            if (notepad is not null)
            {
                try
                {
                    if (!notepad.HasExited)
                    {
                        notepad.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // best-effort cleanup of owned process only
                }
                finally
                {
                    notepad.Dispose();
                }
            }
        }
    }

    private async Task<BenchmarkScenarioResult> RunRobloxHierarchyAsync(BenchmarkOptions options, CancellationToken cancellationToken)
    {
        const string name = "studio-hierarchy";
        var ping = await _client.CallAsync(CommandNames.RobloxPluginPing, new { }, cancellationToken)
            .ConfigureAwait(false);
        if (!ping.Ok)
        {
            return Skipped(name, CommandNames.RobloxGetHierarchy, ping.ErrorMessage ?? "roblox.plugin_ping failed");
        }

        if (ping.Raw is JsonElement pingRaw &&
            pingRaw.TryGetProperty("data", out var pingData) &&
            pingData.TryGetProperty("connected", out var connected) &&
            connected.ValueKind == JsonValueKind.False)
        {
            return Skipped(name, CommandNames.RobloxGetHierarchy, "Roblox Studio plugin not connected");
        }

        var hierarchy = await _client.CallAsync(CommandNames.RobloxGetHierarchy, new { maxDepth = 2 }, cancellationToken)
            .ConfigureAwait(false);
        if (!hierarchy.Ok && IsUnavailable(hierarchy, "connect", "plugin", "bridge"))
        {
            return Skipped(name, CommandNames.RobloxGetHierarchy, hierarchy.ErrorMessage ?? "Roblox Studio plugin not connected");
        }

        var wall = new List<long> { ping.WallMs, hierarchy.WallMs };
        var tool = new List<long>();
        if (ping.ToolDurationMs is long pingTool)
        {
            tool.Add(pingTool);
        }

        if (hierarchy.ToolDurationMs is long hierarchyTool)
        {
            tool.Add(hierarchyTool);
        }

        return BuildResult(
            name,
            CommandNames.RobloxGetHierarchy,
            hierarchy.Ok ? "completed" : "failed",
            hierarchy.ErrorMessage,
            hierarchy.Ok,
            1,
            wall,
            tool);
    }

    private async Task<BenchmarkScenarioResult> RunBlenderComparisonAsync(BenchmarkOptions options, CancellationToken cancellationToken)
    {
        const string name = "blender-batch-vs-individual";
        var batch = await _client.CallAsync(
            CommandNames.BlenderBatch,
            new
            {
                operations = new object[]
                {
                    new { op = "get_scene" },
                    new { op = "get_objects" }
                },
                failFast = true
            },
            cancellationToken).ConfigureAwait(false);

        if (!batch.Ok && IsUnavailable(batch, "executable", "not found", "blender"))
        {
            return Skipped(name, CommandNames.BlenderBatch, batch.ErrorMessage ?? "Blender executable not available");
        }

        var scene = await _client.CallAsync(CommandNames.BlenderGetScene, new { }, cancellationToken).ConfigureAwait(false);
        var objects = await _client.CallAsync(CommandNames.BlenderGetObjects, new { }, cancellationToken).ConfigureAwait(false);
        if (!scene.Ok && IsUnavailable(scene, "executable", "not found", "blender"))
        {
            return Skipped(name, CommandNames.BlenderGetScene, scene.ErrorMessage ?? "Blender executable not available");
        }

        var individualOk = scene.Ok && objects.Ok;
        var individualWall = scene.WallMs + objects.WallMs;
        var batchWall = batch.WallMs;

        return new BenchmarkScenarioResult
        {
            Name = name,
            Method = CommandNames.BlenderBatch,
            Status = batch.Ok && individualOk ? "completed" : batch.Ok || individualOk ? "completed" : "failed",
            SkipReason = null,
            Ok = batch.Ok && individualOk,
            Iterations = 1,
            WallMs = [batchWall, individualWall],
            ToolDurationMs =
            [
                batch.ToolDurationMs ?? batchWall,
                (scene.ToolDurationMs ?? scene.WallMs) + (objects.ToolDurationMs ?? objects.WallMs)
            ],
            P50Ms = Percentile([batchWall, individualWall], 0.50),
            P95Ms = Percentile([batchWall, individualWall], 0.95)
        };
    }

    private static bool IsUnavailable(BenchmarkCallResult result, params string[] hints)
    {
        var text = $"{result.ErrorCode} {result.ErrorMessage}";
        return hints.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static BenchmarkScenarioResult Skipped(string name, string method, string reason) =>
        new()
        {
            Name = name,
            Method = method,
            Status = "skipped",
            SkipReason = reason,
            Ok = null,
            Iterations = 0,
            WallMs = [],
            ToolDurationMs = []
        };

    private static BenchmarkScenarioResult BuildResult(
        string name,
        string method,
        string status,
        string? skipReason,
        bool ok,
        int iterations,
        IReadOnlyList<long> wallMs,
        IReadOnlyList<long> toolDurationMs) =>
        new()
        {
            Name = name,
            Method = method,
            Status = status,
            SkipReason = skipReason,
            Ok = ok,
            Iterations = iterations,
            WallMs = wallMs,
            ToolDurationMs = toolDurationMs,
            P50Ms = wallMs.Count > 0 ? Percentile(wallMs, 0.50) : null,
            P95Ms = wallMs.Count > 0 ? Percentile(wallMs, 0.95) : null
        };

    private static long Percentile(IReadOnlyList<long> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static string? ExtractString(JsonElement? raw, params string[] path)
    {
        if (raw is not JsonElement element)
        {
            return null;
        }

        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
