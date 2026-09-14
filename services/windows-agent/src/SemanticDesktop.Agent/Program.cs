using SemanticDesktop.Agent;
using SemanticDesktop.IPC;

var pipeName = args.FirstOrDefault(a => a.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))?["--pipe=".Length..]
               ?? PipeNames.Default;

CommandDispatcher? dispatcher = null;
await using var server = new NamedPipeServer(pipeName, async (request, ct) =>
{
    dispatcher ??= new CommandDispatcher();
    return await dispatcher.DispatchAsync(request, ct).ConfigureAwait(false);
});
server.Start();

Console.WriteLine($"SemanticDesktop agent listening on pipe '{pipeName}'.");
Console.WriteLine("Press Ctrl+C to exit.");

var exit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exit.TrySetResult();
};
await exit.Task;
dispatcher?.Dispose();
