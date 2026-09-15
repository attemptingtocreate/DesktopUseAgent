using Microsoft.UI.Xaml;
using SemanticDesktop.ControlCenter.Client;

namespace SemanticDesktop.ControlCenter;

public partial class App : Application
{
    private Window? _window;
    public static AgentBridge Bridge { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        var root = FindRepoRoot();
        var agentProject = Path.Combine(root, "services", "windows-agent", "src", "SemanticDesktop.Agent", "SemanticDesktop.Agent.csproj");
        Bridge = new AgentBridge(agentProjectOrDll: File.Exists(agentProject) ? agentProject : null);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
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
