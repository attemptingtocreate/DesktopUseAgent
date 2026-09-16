namespace SemanticDesktop.Adapters.Blender;

public interface IBlenderProcessRunner
{
    Task<BlenderProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken);

    Task<BlenderProcessResult> LaunchGuiAsync(
        string executable,
        string blendPath,
        CancellationToken cancellationToken);
}

public sealed class BlenderProcessResult
{
    public bool Ok { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public int ExitCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static BlenderProcessResult Success(string stdout, string stderr = "") =>
        new() { Ok = true, Stdout = stdout, Stderr = stderr };

    public static BlenderProcessResult Fail(int exitCode, string message, string stdout = "", string stderr = "") =>
        new() { Ok = false, ExitCode = exitCode, ErrorMessage = message, Stdout = stdout, Stderr = stderr };
}

public sealed class DefaultBlenderProcessRunner : IBlenderProcessRunner
{
    public async Task<BlenderProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Blender.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                return BlenderProcessResult.Fail(proc.ExitCode, $"Blender exit {proc.ExitCode}", stdout, stderr);
            }

            return BlenderProcessResult.Success(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            return BlenderProcessResult.Fail(-1, "Blender invocation cancelled.");
        }
        catch (Exception ex)
        {
            return BlenderProcessResult.Fail(-1, ex.Message);
        }
    }

    public Task<BlenderProcessResult> LaunchGuiAsync(
        string executable,
        string blendPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = QuoteArg(blendPath),
                UseShellExecute = true
            };
            _ = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Blender.");
            return Task.FromResult(BlenderProcessResult.Success("GUI_LAUNCHED"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(BlenderProcessResult.Fail(-1, ex.Message));
        }
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;
}
