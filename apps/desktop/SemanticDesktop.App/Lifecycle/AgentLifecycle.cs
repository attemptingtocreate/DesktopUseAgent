using System.Diagnostics;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Tools;
using SemanticDesktop.ControlCenter.Client;

namespace SemanticDesktop.App.Lifecycle;

public interface IAgentProcessGateway
{
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
    Task<int> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(int pid, CancellationToken cancellationToken = default);
}

public sealed class AgentBridgeProcessGateway : IAgentProcessGateway
{
    private readonly AgentBridge _bridge;
    private readonly string? _agentProjectOrDll;
    private readonly string _pipeName;

    public AgentBridgeProcessGateway(AgentBridge bridge, string? agentProjectOrDll = null)
    {
        _bridge = bridge;
        _agentProjectOrDll = agentProjectOrDll;
        _pipeName = bridge.PipeName;
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) => _bridge.PingAsync(cancellationToken);

    public async Task<int> StartAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_agentProjectOrDll))
        {
            await _bridge.EnsureAgentAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (_agentProjectOrDll.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(_agentProjectOrDll);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add($"--pipe={_pipeName}");
        }
        else if (_agentProjectOrDll.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = _agentProjectOrDll;
            psi.ArgumentList.Add($"--pipe={_pipeName}");
        }
        else
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(_agentProjectOrDll);
            psi.ArgumentList.Add($"--pipe={_pipeName}");
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start agent process.");
        for (var i = 0; i < 40; i++)
        {
            if (await PingAsync(cancellationToken).ConfigureAwait(false))
            {
                return process.Id;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for agent to become ready.");
    }

    public Task StopAsync(int pid, CancellationToken cancellationToken = default)
    {
        if (pid <= 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }

        return Task.CompletedTask;
    }
}

public sealed class AgentLifecycle
{
    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private readonly IAgentProcessGateway _gateway;
    private readonly AppSettingsStore _settings;
    private int? _startedPid;

    public AgentLifecycle(IAgentProcessGateway gateway, AppSettingsStore settings)
    {
        _gateway = gateway;
        _settings = settings;
    }

    public int? StartedPid => _startedPid;

    public async Task EnsureAgentAsync(CancellationToken cancellationToken = default)
    {
        await StartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _gateway.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (await _gateway.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            _startedPid = await _gateway.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!await _gateway.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException("Agent did not become ready after start.");
            }
        }
        finally
        {
            StartGate.Release();
        }
    }

    public async Task RecoverIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (await _gateway.PingAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await EnsureAgentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task OnAppExitAsync(CancellationToken cancellationToken = default)
    {
        var behavior = _settings.Get().General.CloseBehavior;
        if (behavior == CloseBehavior.LeaveAgentRunning)
        {
            return;
        }

        if (_startedPid is int pid)
        {
            await _gateway.StopAsync(pid, cancellationToken).ConfigureAwait(false);
        }
    }

    public static McpGatewayStatus ProbeMcpGateway(string? repoRoot = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installRoot = SemanticDesktop.Core.Production.InstallRootResolver.ResolveInstallRoot(
            Environment.GetEnvironmentVariable("DESKTOPUSEAGENT_INSTALL"),
            localAppData,
            AppContext.BaseDirectory);
        if (installRoot is not null)
        {
            var installedEntry = Path.Combine(installRoot, "mcp", "dist", "index.js");
            if (File.Exists(installedEntry))
            {
                return new McpGatewayStatus
                {
                    AvailableAsStdioModule = true,
                    Path = Path.Combine(installRoot, "mcp"),
                    EntryPath = installedEntry,
                    ProductVersion = ReadVersionFile(Path.Combine(installRoot, "VERSION"))
                };
            }
        }

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            roots.Add(repoRoot);
        }

        roots.Add(AppContext.BaseDirectory);
        roots.Add(Environment.CurrentDirectory);
        try
        {
            roots.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")));
            roots.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "mcp")));
        }
        catch
        {
            // ignored
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var devRoot = Path.Combine(root, "apps", "mcp-server");
            var devEntry = Path.Combine(devRoot, "dist", "index.js");
            if (File.Exists(devEntry))
            {
                return new McpGatewayStatus
                {
                    AvailableAsStdioModule = true,
                    Path = devRoot,
                    EntryPath = devEntry,
                    ProductVersion = ReadVersionFile(Path.Combine(root, "VERSION"))
                };
            }
        }

        return new McpGatewayStatus { AvailableAsStdioModule = false };
    }

    private static string? ReadVersionFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class FakeAgentProcessGateway : IAgentProcessGateway
{
    public int PingsBeforeSuccess { get; set; }
    public int PingCount { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int LastPid { get; private set; } = 4242;
    public bool Running { get; set; }

    public Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        PingCount++;
        if (Running)
        {
            return Task.FromResult(true);
        }

        if (PingCount <= PingsBeforeSuccess)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(Running);
    }

    public Task<int> StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        Running = true;
        return Task.FromResult(LastPid);
    }

    public Task StopAsync(int pid, CancellationToken cancellationToken = default)
    {
        StopCount++;
        Running = false;
        return Task.CompletedTask;
    }
}
