using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.VsCode;

public sealed class VsCodeAdapter : IApplicationAdapter
{
    public string Id => "vscode";

    public bool CanHandle(ProcessInfo process)
    {
        var name = process.Name;
        var path = process.Path ?? "";
        return name.Contains("Code", StringComparison.OrdinalIgnoreCase)
               || path.Contains("Microsoft VS Code", StringComparison.OrdinalIgnoreCase)
               || path.Contains("VSCodium", StringComparison.OrdinalIgnoreCase)
               || name.Equals("code.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Code - Insiders.exe", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var cli = FindCodeCli();
        return Task.FromResult(new ApplicationCapabilities
        {
            AdapterId = Id,
            Available = cli is not null,
            Actions = new[]
            {
                CommandNames.VsCodeOpenFile,
                CommandNames.VsCodeOpenFolder,
                CommandNames.VsCodeExecuteCommand,
                CommandNames.VsCodeGetWorkspace
            },
            Meta = new Dictionary<string, object?>
            {
                ["cli"] = cli,
                ["provider"] = "vscode-cli"
            }
        });
    }

    public async Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        return command.Action.ToLowerInvariant() switch
        {
            CommandNames.VsCodeOpenFile => await OpenFileAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.VsCodeOpenFolder => await OpenFolderAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.VsCodeExecuteCommand => await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.VsCodeGetWorkspace => GetWorkspace(command),
            _ => AdapterResult.Fail(ErrorCodes.Unsupported, $"Unknown vscode action '{command.Action}'.")
        };
    }

    private static async Task<AdapterResult> OpenFileAsync(AdapterCommand command, CancellationToken ct)
    {
        var path = GetString(command.Params, "path") ?? GetString(command.Params, "file");
        if (string.IsNullOrWhiteSpace(path))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "path is required.");
        }

        var cli = FindCodeCli();
        if (cli is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "VS Code CLI ('code') not found.");
        }

        var args = new List<string> { "-g", path };
        var line = GetInt(command.Params, "line");
        if (line is > 0)
        {
            args[^1] = $"{path}:{line}";
            var col = GetInt(command.Params, "column");
            if (col is > 0)
            {
                args[^1] = $"{path}:{line}:{col}";
            }
        }

        var run = await RunCliAsync(cli, args, ct).ConfigureAwait(false);
        return run.Ok
            ? AdapterResult.Success(new { opened = path, provider = "vscode-cli" })
            : run;
    }

    private static async Task<AdapterResult> OpenFolderAsync(AdapterCommand command, CancellationToken ct)
    {
        var path = GetString(command.Params, "path") ?? GetString(command.Params, "folder");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "folder path is required.");
        }

        var cli = FindCodeCli();
        if (cli is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "VS Code CLI ('code') not found.");
        }

        var run = await RunCliAsync(cli, new[] { path }, ct).ConfigureAwait(false);
        return run.Ok
            ? AdapterResult.Success(new { opened = path, provider = "vscode-cli" })
            : run;
    }

    private static async Task<AdapterResult> ExecuteCommandAsync(AdapterCommand command, CancellationToken ct)
    {
        var cmd = GetString(command.Params, "command");
        if (string.IsNullOrWhiteSpace(cmd))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "command is required.");
        }

        var cli = FindCodeCli();
        if (cli is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "VS Code CLI ('code') not found.");
        }

        // Prefer CLI command hook; falls back to reporting unsupported when CLI rejects it.
        var run = await RunCliAsync(cli, new[] { "--command", cmd }, ct).ConfigureAwait(false);
        if (run.Ok)
        {
            return AdapterResult.Success(new { command = cmd, provider = "vscode-cli" });
        }

        return AdapterResult.Fail(
            ErrorCodes.AdapterUnavailable,
            "VS Code CLI did not accept --command. Use filesystem/CLI open actions or an extension API. " + (run.Message ?? ""));
    }

    private static AdapterResult GetWorkspace(AdapterCommand command)
    {
        var folder = GetString(command.Params, "path")
                     ?? Environment.GetEnvironmentVariable("VSCODE_WORKSPACE")
                     ?? Environment.GetEnvironmentVariable("PWD")
                     ?? Directory.GetCurrentDirectory();

        var files = Array.Empty<string>();
        try
        {
            if (Directory.Exists(folder))
            {
                files = Directory.GetFiles(folder)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .Cast<string>()
                    .Take(50)
                    .ToArray();
            }
        }
        catch
        {
            // ignore listing failures
        }

        return AdapterResult.Success(new
        {
            workspace = folder,
            files,
            provider = "vscode-cli"
        });
    }

    public static string? FindCodeCli()
    {
        var env = Environment.GetEnvironmentVariable("VSCODE_CLI");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var name in new[] { "code.cmd", "code.exe", "code" })
        {
            var hit = FindOnPath(name);
            if (hit is not null)
            {
                return hit;
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Programs", "Microsoft VS Code", "bin", "code.cmd"),
            Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "bin", "code-insiders.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "bin", "code.cmd")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<AdapterResult> RunCliAsync(string cli, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cli,
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
                ?? throw new InvalidOperationException("Failed to start VS Code CLI.");
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

    private static int? GetInt(Dictionary<string, object?>? parameters, string key)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            JsonElement je when je.TryGetInt32(out var n) => n,
            string s when int.TryParse(s, out var n) => n,
            _ => null
        };
    }

    private static string Truncate(string text) =>
        text.Length <= 800 ? text : text[..800] + "…";
}
