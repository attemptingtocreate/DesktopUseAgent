using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Production;
using SemanticDesktop.IPC;

var pipeName = PipeNames.Resolve(
    args.FirstOrDefault(a => a.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))?["--pipe=".Length..]);
var dataRoot = args.FirstOrDefault(a => a.StartsWith("--data=", StringComparison.OrdinalIgnoreCase))?["--data=".Length..]
               ?? ProductionRuntime.DefaultRoot;

var acquire = AgentSingleInstance.Acquire(pipeName);
switch (acquire.Status)
{
    case AgentSingleInstanceAcquireStatus.AlreadyRunning:
        if (await TryPingExistingAgentAsync(pipeName).ConfigureAwait(false))
        {
            return 0;
        }

        Console.Error.WriteLine(acquire.Message ?? "Another DesktopUseAgent instance is already running for this pipe.");
        return 1;
    case AgentSingleInstanceAcquireStatus.Failed:
        Console.Error.WriteLine(acquire.Message ?? "Failed to acquire agent single-instance mutex.");
        return 2;
}

using var singleInstance = acquire.Instance!;

CommandDispatcher CreateDispatcher()
{
    try
    {
        return new CommandDispatcher(dataRoot);
    }
    catch (Exception ex)
    {
        WriteStartupCrash(dataRoot, ex);
        throw;
    }
}

static void WriteStartupCrash(string root, Exception ex)
{
    try
    {
        var dir = Path.Combine(root, "crashes");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"startup-{DateTime.UtcNow:yyyyMMddHHmmss}.txt"), ex.ToString());
    }
    catch
    {
        // ignore
    }
}

var dispatcher = CreateDispatcher();

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        WriteStartupCrash(dataRoot, ex);
    }
};

TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

await using var server = new NamedPipeServer(pipeName, async (request, ct) =>
{
    try
    {
        return await dispatcher.DispatchAsync(request, ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        WriteStartupCrash(dataRoot, ex);
        try { dispatcher.Dispose(); } catch { /* recycle */ }
        dispatcher = CreateDispatcher();
        return await dispatcher.DispatchAsync(request, ct).ConfigureAwait(false);
    }
});
server.Start();

Console.WriteLine($"DesktopUseAgent agent {RuntimeCompat.ApiVersion} listening on pipe '{pipeName}'.");
Console.WriteLine("Press Ctrl+C to exit.");

var exit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exit.TrySetResult();
};
await exit.Task;
dispatcher.Dispose();
return 0;

static async Task<bool> TryPingExistingAgentAsync(string pipeName)
{
    try
    {
        var result = await NamedPipeClient.CallOnceAsync(
            CommandNames.SystemPing,
            new { },
            CancellationToken.None,
            pipeName,
            connectTimeoutMs: 800,
            responseTimeoutMs: 2000).ConfigureAwait(false);
        return result.TryGetProperty("ok", out var ok) && ok.GetBoolean();
    }
    catch
    {
        return false;
    }
}
