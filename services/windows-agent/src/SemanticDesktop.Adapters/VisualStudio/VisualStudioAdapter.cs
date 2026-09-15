using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.VisualStudio;

public sealed class VisualStudioAdapter : IApplicationAdapter
{
    public string Id => "visualstudio";

    public bool CanHandle(ProcessInfo process)
    {
        var name = process.Name;
        var path = process.Path ?? "";
        return name.Contains("devenv", StringComparison.OrdinalIgnoreCase)
               || path.Contains("Microsoft Visual Studio", StringComparison.OrdinalIgnoreCase)
               || name.Equals("devenv.exe", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var devenv = FindDevenv();
        var msbuild = FindMsBuild();
        return Task.FromResult(new ApplicationCapabilities
        {
            AdapterId = Id,
            Available = devenv is not null || msbuild is not null,
            Actions = new[]
            {
                CommandNames.VisualStudioGetSolution,
                CommandNames.VisualStudioBuild,
                CommandNames.VisualStudioOpenFile,
                CommandNames.VisualStudioOpenSolution
            },
            Meta = new Dictionary<string, object?>
            {
                ["devenv"] = devenv,
                ["msbuild"] = msbuild,
                ["provider"] = "visualstudio-cli"
            }
        });
    }

    public async Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        return command.Action.ToLowerInvariant() switch
        {
            CommandNames.VisualStudioGetSolution => GetSolution(command),
            CommandNames.VisualStudioBuild => await BuildAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.VisualStudioOpenFile => await OpenFileAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.VisualStudioOpenSolution => await OpenSolutionAsync(command, cancellationToken).ConfigureAwait(false),
            _ => AdapterResult.Fail(ErrorCodes.Unsupported, $"Unknown visualstudio action '{command.Action}'.")
        };
    }

    private static AdapterResult GetSolution(AdapterCommand command)
    {
        var path = GetString(command.Params, "path") ?? GetString(command.Params, "solution");
        path ??= FindSolutionFromRunningDevenv();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return AdapterResult.Fail(ErrorCodes.NotFound, "Solution path not provided and none detected from devenv.");
        }

        var projects = new List<object>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var m = Regex.Match(line, "^Project\\(\"[^\"]+\"\\)\\s*=\\s*\"([^\"]+)\",\\s*\"([^\"]+)\"");
                if (m.Success)
                {
                    projects.Add(new { name = m.Groups[1].Value, path = m.Groups[2].Value });
                }
            }
        }
        catch (Exception ex)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterFailed, ex.Message);
        }

        return AdapterResult.Success(new
        {
            solution = path,
            projects,
            provider = "visualstudio-cli"
        });
    }

    private static async Task<AdapterResult> BuildAsync(AdapterCommand command, CancellationToken ct)
    {
        var solution = GetString(command.Params, "path") ?? GetString(command.Params, "solution") ?? FindSolutionFromRunningDevenv();
        if (string.IsNullOrWhiteSpace(solution) || !File.Exists(solution))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "solution path is required.");
        }

        var config = GetString(command.Params, "configuration") ?? "Debug";
        var msbuild = FindMsBuild();
        if (msbuild is not null)
        {
            var run = await RunAsync(msbuild, new[] { solution, $"/p:Configuration={config}", "/m" }, ct).ConfigureAwait(false);
            return run.Ok
                ? AdapterResult.Success(new { built = solution, configuration = config, provider = "msbuild" })
                : run;
        }

        var devenv = FindDevenv();
        if (devenv is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "Neither MSBuild nor devenv found.");
        }

        var devenvRun = await RunAsync(devenv, new[] { solution, "/Build", config }, ct).ConfigureAwait(false);
        return devenvRun.Ok
            ? AdapterResult.Success(new { built = solution, configuration = config, provider = "devenv" })
            : devenvRun;
    }

    private static async Task<AdapterResult> OpenFileAsync(AdapterCommand command, CancellationToken ct)
    {
        var path = GetString(command.Params, "path") ?? GetString(command.Params, "file");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "file path is required.");
        }

        var devenv = FindDevenv();
        if (devenv is null)
        {
            // Fallback: shell-open with default editor association still routes through OS, not UIA menus.
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return AdapterResult.Success(new { opened = path, provider = "shell" });
            }
            catch (Exception ex)
            {
                return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "devenv not found: " + ex.Message);
            }
        }

        var run = await RunAsync(devenv, new[] { "/Edit", path }, ct).ConfigureAwait(false);
        return run.Ok
            ? AdapterResult.Success(new { opened = path, provider = "devenv" })
            : run;
    }

    private static async Task<AdapterResult> OpenSolutionAsync(AdapterCommand command, CancellationToken ct)
    {
        var path = GetString(command.Params, "path") ?? GetString(command.Params, "solution");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "solution path is required.");
        }

        var devenv = FindDevenv();
        if (devenv is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "devenv not found.");
        }

        var run = await RunAsync(devenv, new[] { path }, ct).ConfigureAwait(false);
        return run.Ok
            ? AdapterResult.Success(new { opened = path, provider = "devenv" })
            : run;
    }

    public static string? FindDevenv()
    {
        var env = Environment.GetEnvironmentVariable("DEVENV_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    ArgumentList = { "-latest", "-products", "*", "-property", "productPath" },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(5000);
                    if (File.Exists(output))
                    {
                        return output;
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        return FindOnPath("devenv.exe");
    }

    public static string? FindMsBuild()
    {
        var env = Environment.GetEnvironmentVariable("MSBUILD_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    ArgumentList =
                    {
                        "-latest", "-requires", "Microsoft.Component.MSBuild",
                        "-find", @"MSBuild\**\Bin\MSBuild.exe"
                    },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    var output = proc.StandardOutput.ReadToEnd()
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault();
                    proc.WaitForExit(5000);
                    if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
                    {
                        return output;
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        return FindOnPath("msbuild.exe");
    }

    private static string? FindSolutionFromRunningDevenv()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("devenv"))
            {
                try
                {
                    var cmd = proc.MainModule?.FileName;
                    // Command line not always available; scan open window title for .sln is unreliable.
                    _ = cmd;
                }
                catch
                {
                    // access denied
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static async Task<AdapterResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start process.");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                return AdapterResult.Fail(ErrorCodes.AdapterFailed, Truncate(stderr + stdout));
            }

            return AdapterResult.Success(stdout);
        }
        catch (Exception ex)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterFailed, ex.Message);
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string? GetString(Dictionary<string, object?>? parameters, string key)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => value.ToString()
        };
    }

    private static string Truncate(string text) =>
        text.Length <= 800 ? text : text[..800] + "…";
}
