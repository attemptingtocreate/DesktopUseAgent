using Microsoft.UI.Xaml;
using SemanticDesktop.App;
using SemanticDesktop.App.Runtime;
using SemanticDesktop.ControlCenter.Client;

namespace SemanticDesktop.ControlCenter;

public partial class App : Application
{
    private Window? _window;
    public static AgentBridge Bridge { get; private set; } = null!;
    public static AppHost Host { get; private set; } = null!;
    public static CompositeAgentRuntimeObserver Observer { get; } = new();

    public App()
    {
        InitializeComponent();
        var root = FindRepoRoot();
        var agentProject = Path.Combine(root, "services", "windows-agent", "src", "SemanticDesktop.Agent", "SemanticDesktop.Agent.csproj");
        var launchPath = File.Exists(agentProject) ? agentProject : FindPublishedAgent();
        Bridge = new AgentBridge(agentProjectOrDll: launchPath);
        Host = new AppHost(
            SemanticDesktop.App.Persistence.DataRootResolver.Resolve(),
            observer: Observer,
            agentProjectOrDll: launchPath,
            repoRoot: root);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Closed += Window_Closed;
        _window.Activate();
        try
        {
            if (Host.Lifecycle is not null)
            {
                await Host.Lifecycle.EnsureAgentAsync();
            }
            else
            {
                await Bridge.EnsureAgentAsync();
            }
        }
        catch
        {
            // MainWindow surfaces agent status.
        }
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            if (Host.Lifecycle is not null)
            {
                await Host.Lifecycle.OnAppExitAsync();
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string? FindPublishedAgent()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "agent", "DesktopUseAgent.Agent.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "DesktopUseAgent.Agent.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "agent", "DesktopUseAgent.Agent.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "agent", "SemanticDesktop.Agent.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "SemanticDesktop.Agent.dll"))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MASTER_SPEC.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
