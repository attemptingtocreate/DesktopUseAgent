using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Cli;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

var pipeName = PipeNames.Resolve();
var commandArgs = args.ToList();
for (var i = 0; i < commandArgs.Count; i++)
{
    if (commandArgs[i].StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))
    {
        pipeName = PipeNames.Resolve(commandArgs[i]["--pipe=".Length..]);
        commandArgs.RemoveAt(i);
        break;
    }
}

if (commandArgs.Count == 0 || commandArgs[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var command = commandArgs[0].ToLowerInvariant();
if (command == "agent")
{
    return await RunAgentHostAsync(pipeName).ConfigureAwait(false);
}

if (command == "benchmark")
{
    return await RunBenchmarkAsync(pipeName, commandArgs.Skip(1).ToList()).ConfigureAwait(false);
}

try
{
    await EnsureAgentAsync(pipeName).ConfigureAwait(false);
    await using var client = new NamedPipeClient(pipeName);
    await client.ConnectAsync(CancellationToken.None, timeoutMs: 10000).ConfigureAwait(false);

    if (command == "call")
    {
        return await RunCallAsync(client, commandArgs.Skip(1).ToList()).ConfigureAwait(false);
    }

    await DispatchClientCommandAsync(client, command, commandArgs).ConfigureAwait(false);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> RunAgentHostAsync(string pipeName)
{
    var agentDll = FindAgentDll()
                   ?? throw new FileNotFoundException("DesktopUseAgent.Agent.dll not found. Build the solution.");
    var psi = new ProcessStartInfo("dotnet", $"\"{agentDll}\" --pipe={pipeName}")
    {
        UseShellExecute = false
    };
    using var process = Process.Start(psi);
    if (process is null)
    {
        return 1;
    }

    await process.WaitForExitAsync().ConfigureAwait(false);
    return process.ExitCode;
}

static async Task EnsureAgentAsync(string pipeName)
{
    if (await PingAsync(pipeName, 500).ConfigureAwait(false))
    {
        return;
    }

    var agentDll = FindAgentDll()
                   ?? throw new FileNotFoundException("DesktopUseAgent.Agent.dll not found. Build the solution.");

    var psi = new ProcessStartInfo("dotnet", $"\"{agentDll}\" --pipe={pipeName}")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };
    _ = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start agent.");

    for (var i = 0; i < 50; i++)
    {
        if (await PingAsync(pipeName, 400).ConfigureAwait(false))
        {
            return;
        }

        await Task.Delay(100).ConfigureAwait(false);
    }

    throw new TimeoutException("Timed out waiting for DesktopUseAgent agent pipe.");
}

static async Task<bool> PingAsync(string pipeName, int timeoutMs)
{
    try
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await using var client = new NamedPipeClient(pipeName);
        await client.ConnectAsync(cts.Token, timeoutMs).ConfigureAwait(false);
        var result = await client.SendAsync(CommandNames.SystemPing, new { }, cts.Token).ConfigureAwait(false);
        return result.TryGetProperty("ok", out var ok) && ok.GetBoolean();
    }
    catch
    {
        return false;
    }
}

static string? FindAgentDll()
{
    var names = new[] { "DesktopUseAgent.Agent.dll", "SemanticDesktop.Agent.dll" };
    var searchDirs = new[]
    {
        AppContext.BaseDirectory,
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SemanticDesktop.Agent", "bin", "Debug", "net8.0-windows")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SemanticDesktop.Agent", "bin", "Release", "net8.0-windows"))
    };
    foreach (var dir in searchDirs)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                return path;
            }
        }
    }

    return null;
}

static async Task DispatchClientCommandAsync(NamedPipeClient client, string command, List<string> commandArgs)
{
    if (command is "plan" or "plan-execute")
    {
        var plan = BuildPlanParams(commandArgs);
        var result = await client.SendAsync(CommandNames.PlanExecute, plan, CancellationToken.None).ConfigureAwait(false);
        PrintPerformance(result);
        PrintJson(result);
        return;
    }

    if (command is "emergency-stop")
    {
        var result = await client.SendAsync(CommandNames.SystemEmergencyStop, new { }, CancellationToken.None).ConfigureAwait(false);
        PrintJson(result);
        return;
    }

    if (command is "emergency-clear")
    {
        var result = await client.SendAsync(CommandNames.SystemEmergencyStopClear, new { }, CancellationToken.None).ConfigureAwait(false);
        PrintJson(result);
        return;
    }

    if (command is "audit")
    {
        var result = await client.SendAsync(CommandNames.AuditList, new { take = 50 }, CancellationToken.None).ConfigureAwait(false);
        PrintJson(result);
        return;
    }

    object parameters = command switch
    {
        "windows" => new { },
        "tree" => BuildTreeParams(commandArgs),
        "find" => BuildFindParams(commandArgs),
        "set-value" => commandArgs.Count >= 3
            ? new { elementId = commandArgs[1], value = string.Join(' ', commandArgs.Skip(2)) }
            : throw new ArgumentException("Usage: semantic-desktop set-value <elementId> <value>"),
        "invoke" => commandArgs.Count >= 2
            ? new { elementId = commandArgs[1] }
            : throw new ArgumentException("Usage: semantic-desktop invoke <elementId>"),
        "get-text" => commandArgs.Count >= 2
            ? new { elementId = commandArgs[1] }
            : throw new ArgumentException("Usage: semantic-desktop get-text <elementId>"),
        "launch" => commandArgs.Count >= 2
            ? new { executable = commandArgs[1], args = commandArgs.Skip(2).ToArray() }
            : throw new ArgumentException("Usage: semantic-desktop launch <executable> [args...]"),
        "focus" => commandArgs.Count >= 2
            ? new { windowId = commandArgs[1] }
            : throw new ArgumentException("Usage: semantic-desktop focus <windowId>"),
        _ => throw new ArgumentException($"Unknown command: {command}")
    };

    var method = command switch
    {
        "windows" => CommandNames.WindowList,
        "tree" => CommandNames.UiGetTree,
        "find" => CommandNames.UiFind,
        "set-value" => CommandNames.UiSetValue,
        "invoke" => CommandNames.UiInvoke,
        "get-text" => CommandNames.UiGetText,
        "launch" => CommandNames.ProcessLaunch,
        "focus" => CommandNames.WindowFocus,
        _ => throw new ArgumentException($"Unknown command: {command}")
    };

    var result2 = await client.SendAsync(method, parameters, CancellationToken.None).ConfigureAwait(false);
    PrintPerformance(result2);
    switch (command)
    {
        case "windows":
            PrintWindows(result2);
            break;
        case "find":
            PrintFind(result2);
            break;
        default:
            PrintJson(result2);
            break;
    }
}

static object BuildPlanParams(List<string> commandArgs)
{
    // semantic-desktop plan demo
    // semantic-desktop plan <path-to-json>
    if (commandArgs.Count >= 2 &&
        !string.Equals(commandArgs[1], "demo", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(commandArgs[1]))
    {
        var json = File.ReadAllText(commandArgs[1]);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    var savePath = Path.Combine(Path.GetTempPath(), "semantic-desktop-phase2.txt");
    return new
    {
        name = "Open Notepad and write text",
        options = new { stopOnFailure = true, defaultTimeoutMs = 30000 },
        steps = new object[]
        {
            new
            {
                id = "launch",
                action = "process.launch",
                args = new { executable = "notepad.exe" },
                waitAfter = new { type = "window.exists", process = "notepad.exe", timeoutMs = 15000 }
            },
            new
            {
                id = "find-editor",
                action = "ui.find",
                args = new { controlType = "Document" },
                retries = new { count = 2, delayMs = 300 }
            },
            new
            {
                id = "set-text",
                action = "ui.set_value",
                args = new { targetFrom = "find-editor", value = "Hello from DesktopUseAgent" }
            },
            new
            {
                id = "save",
                action = "filesystem.write_text",
                args = new { path = savePath, contents = "Hello from DesktopUseAgent" },
                waitAfter = new { type = "file.exists", path = savePath, timeoutMs = 5000 }
            }
        }
    };
}

static object BuildTreeParams(List<string> commandArgs)
{
    if (commandArgs.Count < 2)
    {
        throw new ArgumentException("Usage: semantic-desktop tree <windowId> [--depth N]");
    }

    var depth = 3;
    for (var i = 2; i < commandArgs.Count - 1; i++)
    {
        if (commandArgs[i] == "--depth" && int.TryParse(commandArgs[i + 1], out var d))
        {
            depth = d;
        }
    }

    return new { windowId = commandArgs[1], depth, maxNodes = 200, includeBounds = true };
}

static object BuildFindParams(List<string> commandArgs)
{
    if (commandArgs.Count < 2)
    {
        throw new ArgumentException("Usage: semantic-desktop find <windowId> [--type TYPE] [--name NAME] [--automation-id ID]");
    }

    string? type = null;
    string? name = null;
    string? automationId = null;
    for (var i = 2; i < commandArgs.Count; i++)
    {
        switch (commandArgs[i])
        {
            case "--type" when i + 1 < commandArgs.Count:
                type = commandArgs[++i];
                break;
            case "--name" when i + 1 < commandArgs.Count:
                name = commandArgs[++i];
                break;
            case "--automation-id" when i + 1 < commandArgs.Count:
                automationId = commandArgs[++i];
                break;
        }
    }

    return new { windowId = commandArgs[1], type, name, automationId };
}

static void PrintPerformance(JsonElement result)
{
    if (!result.TryGetProperty("performance", out var perf))
    {
        return;
    }

    var operation = perf.TryGetProperty("operation", out var op) ? op.GetString() : "?";
    var duration = perf.TryGetProperty("durationMs", out var d) ? d.GetInt64() : 0;
    var inspected = perf.TryGetProperty("elementsInspected", out var e) ? e.GetInt32() : 0;
    var cache = perf.TryGetProperty("cacheHit", out var c) && c.ValueKind != JsonValueKind.Null
        ? c.GetBoolean().ToString().ToLowerInvariant()
        : "n/a";
    var provider = perf.TryGetProperty("provider", out var p) ? p.GetString() : "?";
    Console.WriteLine($"{operation}");
    Console.WriteLine($"{duration} ms");
    Console.WriteLine($"{inspected} nodes inspected");
    Console.WriteLine($"cache hit: {cache}");
    Console.WriteLine($"provider: {provider}");
    Console.WriteLine();
}

static void PrintWindows(JsonElement result)
{
    if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
    {
        PrintJson(result);
        return;
    }

    Console.WriteLine($"{"ID",-22} {"PROCESS",-18} TITLE");
    if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
    {
        return;
    }

    foreach (var window in data.EnumerateArray())
    {
        var id = window.GetProperty("id").GetString();
        var process = window.GetProperty("process").GetString();
        var title = window.GetProperty("title").GetString();
        Console.WriteLine($"{id,-22} {process,-18} {title}");
    }
}

static void PrintFind(JsonElement result)
{
    if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
    {
        PrintJson(result);
        return;
    }

    if (!result.TryGetProperty("data", out var data) || !data.TryGetProperty("elements", out var elements))
    {
        PrintJson(result);
        return;
    }

    foreach (var el in elements.EnumerateArray())
    {
        var id = el.GetProperty("id").GetString();
        var type = el.TryGetProperty("controlType", out var t) ? t.GetString() : "";
        var name = el.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() : "";
        var autoId = el.TryGetProperty("automationId", out var a) && a.ValueKind != JsonValueKind.Null ? a.GetString() : "";
        Console.WriteLine($"{id}\t{type}\t{name}\t{autoId}");
    }
}

static void PrintJson(JsonElement result)
{
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }));
}

static async Task<int> RunBenchmarkAsync(string pipeName, List<string> args)
{
    var options = ParseBenchmarkOptions(args);
    if (options.DryRun)
    {
        var plan = new BenchmarkRunner(new NoopBenchmarkAgentClient()).CreateDryRunPlan(options);
        Console.WriteLine(JsonSerializer.Serialize(plan, BenchmarkJsonOptions()));
        return 0;
    }

    await EnsureAgentAsync(pipeName).ConfigureAwait(false);
    await using var client = new PipeBenchmarkAgentClient(pipeName);
    await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
    var summary = await new BenchmarkRunner(client).RunAsync(options, CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine(JsonSerializer.Serialize(summary, BenchmarkJsonOptions()));
    return summary.Scenarios.Any(s => s.Status == "failed") ? 1 : 0;
}

static async Task<int> RunCallAsync(NamedPipeClient client, List<string> args)
{
    if (args.Count == 0)
    {
        throw new ArgumentException("Usage: semantic-desktop call <method> [json]");
    }

    var method = args[0];
    JsonElement? parameters = null;
    if (args.Count > 1)
    {
        parameters = JsonSerializer.Deserialize<JsonElement>(string.Join(' ', args.Skip(1)));
    }

    var started = DateTimeOffset.UtcNow;
    var result = await client.SendAsync(method, parameters, CancellationToken.None).ConfigureAwait(false);
    var envelope = new
    {
        method,
        wallMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
        result
    };
    Console.WriteLine(JsonSerializer.Serialize(envelope, BenchmarkJsonOptions()));
    return result.TryGetProperty("ok", out var ok) && ok.GetBoolean() ? 0 : 1;
}

static BenchmarkOptions ParseBenchmarkOptions(List<string> args)
{
    var options = new BenchmarkOptions();
    for (var i = 0; i < args.Count; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--dry-run":
                options = options with { DryRun = true };
                break;
            case "--live":
                options = options with { Live = true };
                break;
            case "--iterations" when i + 1 < args.Count && int.TryParse(args[++i], out var iterations):
                options = options with { Iterations = Math.Max(1, iterations) };
                break;
            case "--monitor" when i + 1 < args.Count && int.TryParse(args[++i], out var monitor):
                options = options with { Monitor = monitor };
                break;
            case "--url" when i + 1 < args.Count:
                options = options with { Url = args[++i] };
                break;
        }
    }

    return options;
}

static JsonSerializerOptions BenchmarkJsonOptions() =>
    new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

static void PrintHelp()
{
    Console.WriteLine("""
        DesktopUseAgent CLI (semantic-desktop)

        Commands:
          agent
          call <method> [json]
          benchmark [--dry-run] [--live] [--iterations N] [--monitor M] [--url URL]
          windows
          focus <windowId>
          tree <windowId> [--depth N]
          find <windowId> [--type TYPE] [--name NAME] [--automation-id ID]
          set-value <elementId> <value>
          invoke <elementId>
          get-text <elementId>
          launch <executable> [args...]
          plan [demo|<plan.json>]
          audit
          emergency-stop
          emergency-clear

        Options:
          --pipe=<name>   Named pipe (default: semantic-desktop-agent)

        Notes:
          Commands auto-start a background agent on the named pipe if needed.
          Element/window handles remain valid while that agent process is alive.
          `plan demo` runs launch→wait→find→set-value→save→file.exists locally.
          `benchmark --dry-run` prints the scenario plan without contacting the agent.
        """);
}

