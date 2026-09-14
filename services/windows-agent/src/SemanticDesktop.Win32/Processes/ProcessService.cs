using System.Diagnostics;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Contracts;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Win32.Processes;

public sealed class ProcessService : IProcessService
{
    private readonly HandleRegistry _handles;

    public ProcessService(HandleRegistry handles)
    {
        _handles = handles;
    }

    public Task<ProcessInfo> LaunchAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Executable))
        {
            throw new ArgumentException("Executable is required.", nameof(request));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Environment.CurrentDirectory
                : request.WorkingDirectory,
            UseShellExecute = true
        };

        if (request.Args is { Length: > 0 })
        {
            startInfo.Arguments = string.Join(" ", request.Args.Select(QuoteArg));
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start process.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{ErrorCodes.ProcessFailed}: {ex.Message}", ex);
        }

        var id = _handles.Allocate(HandleKind.Process, process.Id, new Dictionary<string, object?>
        {
            ["pid"] = process.Id,
            ["name"] = process.ProcessName
        });

        string? path = null;
        try
        {
            path = process.MainModule?.FileName;
        }
        catch
        {
            // access may be denied for some processes
        }

        var info = new ProcessInfo
        {
            Id = id,
            Pid = process.Id,
            Name = process.ProcessName + ".exe",
            Path = path
        };

        return Task.FromResult(info);
    }

    public Task<IReadOnlyList<ProcessInfo>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = new List<ProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(process.ProcessName))
                {
                    continue;
                }

                var id = _handles.Allocate(HandleKind.Process, process.Id, new Dictionary<string, object?>
                {
                    ["pid"] = process.Id,
                    ["name"] = process.ProcessName
                }, preferredId: "proc_" + process.Id.ToString("x"));

                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // ignored
                }

                list.Add(new ProcessInfo
                {
                    Id = id,
                    Pid = process.Id,
                    Name = process.ProcessName + ".exe",
                    Path = path
                });
            }
            catch
            {
                // process may have exited
            }
            finally
            {
                process.Dispose();
            }
        }

        return Task.FromResult<IReadOnlyList<ProcessInfo>>(list.OrderBy(p => p.Name).ToList());
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;
}
