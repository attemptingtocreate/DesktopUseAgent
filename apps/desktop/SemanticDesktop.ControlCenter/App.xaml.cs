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
        UnhandledException += App_UnhandledException;
        try
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
        catch (Exception ex)
        {
            StartupCrashLogger.Log("App construction", ex);
            throw;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Closed += Window_Closed;
            _window.Activate();
        }
        catch (Exception ex)
        {
            StartupCrashLogger.Log("MainWindow construction or activation", ex);
            throw;
        }

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

        try
        {
            await Host.OpenAiTunnel.StartIfEnabledAsync();
        }
        catch
        {
            // Settings surfaces tunnel status.
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupCrashLogger.Log("Application.UnhandledException", e.Exception);
        e.Handled = false;
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            await Host.OpenAiTunnel.StopAsync();
        }
        catch
        {
            // ignore
        }

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

internal static class StartupCrashLogger
{
    private static readonly object Sync = new();

    public static void Log(string stage, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DesktopUseAgent",
                    "crashes");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"ui-startup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
                File.WriteAllText(
                    path,
                    $"Stage: {stage}{Environment.NewLine}" +
                    $"Timestamp: {DateTimeOffset.Now:O}{Environment.NewLine}" +
                    $"Executable: {Environment.ProcessPath}{Environment.NewLine}" +
                    $"Base directory: {AppContext.BaseDirectory}{Environment.NewLine}{Environment.NewLine}" +
                    exception);
            }
        }
        catch
        {
            // Logging must never replace the original startup exception.
        }
    }
}
