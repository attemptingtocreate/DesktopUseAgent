using SemanticDesktop.Agent;
using SemanticDesktop.Core.Production;
using SemanticDesktop.IPC;

var pipeName = PipeNames.Resolve(
    args.FirstOrDefault(a => a.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))?["--pipe=".Length..]);
var dataRoot = args.FirstOrDefault(a => a.StartsWith("--data=", StringComparison.OrdinalIgnoreCase))?["--data=".Length..]
               ?? ProductionRuntime.DefaultRoot;

CommandDispatcher CreateDispatcher()
{
    try
    {
        return new CommandDispatcher(dataRoot);
    }
    catch (Exception ex)
    {
        WriteStartupCrash(dataRoot, ex);
        return new CommandDispatcher();
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
