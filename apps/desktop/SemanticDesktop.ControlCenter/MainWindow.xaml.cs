using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SemanticDesktop.ControlCenter.Client;
using SemanticDesktop.Core.Commands;

namespace SemanticDesktop.ControlCenter;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _timer;
    private ControlCenterSnapshot _snapshot = ControlCenterSnapshot.Disconnected(App.Bridge.PipeName);
    private string _section = "dashboard";
    private string _emergencyHotkey = "Ctrl+Alt+Shift+Esc";
    private TextBox? _inspectorWindowId;
    private TextBox? _inspectorQuery;
    private TextBlock? _inspectorOutput;
    private TextBox? _appProcessBox;
    private ComboBox? _appObserveBox;
    private ComboBox? _appInteractBox;
    private TextBox? _pathBox;
    private CheckBox? _pathRead;
    private CheckBox? _pathWrite;
    private CheckBox? _pathDeny;
    private ComboBox? _capabilityBox;
    private ComboBox? _capabilityDecisionBox;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = false;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += async (_, _) => await RefreshAsync(silent: true);
        _timer.Start();
        _ = BootstrapAsync();
    }

    private async Task BootstrapAsync()
    {
        try
        {
            await App.Bridge.EnsureAgentAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Agent offline: {ex.Message}";
        }

        await RefreshAsync(silent: false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(silent: false);

    private async void Emergency_Click(object sender, RoutedEventArgs e) => await ToggleEmergencyAsync();

    private async void EmergencyHotkey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ToggleEmergencyAsync();
    }

    private async Task ToggleEmergencyAsync()
    {
        try
        {
            if (_snapshot.EmergencyStopped)
            {
                await App.Bridge.CallAsync(CommandNames.SystemEmergencyStopClear, new { });
            }
            else
            {
                await App.Bridge.CallAsync(CommandNames.SystemEmergencyStop, new { });
            }

            await RefreshAsync(silent: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Emergency action failed: {ex.Message}";
        }
    }

    private async Task RefreshAsync(bool silent)
    {
        try
        {
            _snapshot = await App.Bridge.RefreshAsync();
            StatusText.Text = _snapshot.Connected
                ? $"Connected · pipe {_snapshot.PipeName} · sessions {_snapshot.SessionCount} · pending {_snapshot.PendingApprovals}"
                : $"Disconnected · pipe {_snapshot.PipeName}";
            EmergencyButton.Content = _snapshot.EmergencyStopped ? "CLEAR STOP" : "EMERGENCY STOP";
            EmergencyButton.Background = _snapshot.EmergencyStopped
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange)
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 180, 35, 24));
            RenderSection();
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                StatusText.Text = $"Refresh failed: {ex.Message}";
            }
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            _section = tag;
            RenderSection();
        }
    }

    private void RenderSection()
    {
        ContentHost.Children.Clear();
        switch (_section)
        {
            case "dashboard":
                RenderDashboard();
                break;
            case "activity":
                RenderActivity();
                break;
            case "permissions":
                RenderPermissions();
                break;
            case "connections":
                RenderConnections();
                break;
            case "logs":
                RenderLogs();
                break;
            case "inspector":
                RenderInspector();
                break;
            case "settings":
                RenderSettings();
                break;
        }
    }

    private void RenderDashboard()
    {
        ContentHost.Children.Add(Header("Dashboard"));
        ContentHost.Children.Add(Card($"Agent IPC: {(_snapshot.Connected ? "Online" : "Offline")}"));
        ContentHost.Children.Add(Card($"Emergency stop: {(_snapshot.EmergencyStopped ? "ACTIVE" : "cleared")}"));
        ContentHost.Children.Add(Card($"Active sessions: {_snapshot.SessionCount}"));
        ContentHost.Children.Add(Card($"MCP session detected: {(_snapshot.McpSessionPresent ? "yes" : "no (start MCP client against agent)")}"));
        ContentHost.Children.Add(Card($"Pending permission approvals: {_snapshot.PendingApprovals}"));
        ContentHost.Children.Add(Header("Recent activity"));
        foreach (var entry in _snapshot.AuditEntries.Take(8))
        {
            ContentHost.Children.Add(Card($"{entry.Timestamp} · {entry.Action} · {(entry.Success ? "ok" : "fail")} · {entry.Client}"));
        }
    }

    private void RenderActivity()
    {
        ContentHost.Children.Add(Header("Live activity (audit stream)"));
        if (_snapshot.AuditEntries.Count == 0)
        {
            ContentHost.Children.Add(Card("No activity yet."));
            return;
        }

        foreach (var entry in _snapshot.AuditEntries)
        {
            ContentHost.Children.Add(Card(
                $"{entry.Action}\n{entry.Timestamp} · session {entry.SessionId} · {entry.Decision} · {(entry.Success ? "success" : entry.ErrorCode ?? "failed")}"));
        }
    }

    private void RenderPermissions()
    {
        ContentHost.Children.Add(Header("Pending approvals"));
        if (_snapshot.Approvals.Count == 0)
        {
            ContentHost.Children.Add(Card("No pending approvals. ASK capabilities surface here when AutoApproveAsk is false."));
        }
        else
        {
            foreach (var approval in _snapshot.Approvals)
            {
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(new TextBlock
                {
                    Text = $"{approval.Action} · {approval.Capability} · risk {approval.Risk}\n{approval.Reason}\n{approval.Target}"
                });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var once = new Button { Content = "Allow once", Tag = (approval.Id, "once") };
                once.Click += Approve_Click;
                var session = new Button { Content = "Allow session", Tag = (approval.Id, "session") };
                session.Click += Approve_Click;
                var always = new Button { Content = "Allow always", Tag = (approval.Id, "always") };
                always.Click += Approve_Click;
                var deny = new Button { Content = "Deny", Tag = approval.Id };
                deny.Click += Deny_Click;
                row.Children.Add(once);
                row.Children.Add(session);
                row.Children.Add(always);
                row.Children.Add(deny);
                panel.Children.Add(row);
                ContentHost.Children.Add(Wrap(panel));
            }
        }

        ContentHost.Children.Add(Header("Capability defaults"));
        _capabilityBox = new ComboBox { Width = 280, PlaceholderText = "Capability" };
        foreach (var cap in _snapshot.Capabilities)
        {
            _capabilityBox.Items.Add(cap.Capability);
        }

        if (_capabilityBox.Items.Count > 0)
        {
            _capabilityBox.SelectedIndex = 0;
        }

        _capabilityDecisionBox = DecisionCombo("Ask");
        var capRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        capRow.Children.Add(_capabilityBox);
        capRow.Children.Add(_capabilityDecisionBox);
        var capSave = new Button { Content = "Save capability" };
        capSave.Click += SaveCapability_Click;
        capRow.Children.Add(capSave);
        ContentHost.Children.Add(Wrap(capRow));

        foreach (var cap in _snapshot.Capabilities)
        {
            ContentHost.Children.Add(Card($"{cap.Capability} = {cap.Decision}"));
        }

        ContentHost.Children.Add(Header("Per-application permissions"));
        _appProcessBox = new TextBox { PlaceholderText = "Process name (e.g. notepad)", Width = 220 };
        _appObserveBox = DecisionCombo("Allow");
        _appInteractBox = DecisionCombo("Ask");
        var appRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        appRow.Children.Add(_appProcessBox);
        appRow.Children.Add(new TextBlock { Text = "Observe", VerticalAlignment = VerticalAlignment.Center });
        appRow.Children.Add(_appObserveBox);
        appRow.Children.Add(new TextBlock { Text = "Interact", VerticalAlignment = VerticalAlignment.Center });
        appRow.Children.Add(_appInteractBox);
        var appSave = new Button { Content = "Upsert app rule" };
        appSave.Click += SaveApp_Click;
        appRow.Children.Add(appSave);
        ContentHost.Children.Add(Wrap(appRow));
        foreach (var rule in _snapshot.AppRules)
        {
            ContentHost.Children.Add(Card($"{rule.ProcessName} · observe={rule.Observe} · interact={rule.Interact}"));
        }

        ContentHost.Children.Add(Header("Filesystem permission scopes"));
        _pathBox = new TextBox { PlaceholderText = @"Path prefix (e.g. C:\Users\...)", Width = 360 };
        _pathRead = new CheckBox { Content = "Read", IsChecked = true };
        _pathWrite = new CheckBox { Content = "Write", IsChecked = false };
        _pathDeny = new CheckBox { Content = "Deny", IsChecked = false };
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pathRow.Children.Add(_pathBox);
        pathRow.Children.Add(_pathRead);
        pathRow.Children.Add(_pathWrite);
        pathRow.Children.Add(_pathDeny);
        var pathSave = new Button { Content = "Upsert path rule" };
        pathSave.Click += SavePath_Click;
        pathRow.Children.Add(pathSave);
        ContentHost.Children.Add(Wrap(pathRow));
        foreach (var rule in _snapshot.PathRules)
        {
            ContentHost.Children.Add(Card($"{rule.PathPrefix} · read={rule.AllowRead} write={rule.AllowWrite} deny={rule.Deny}"));
        }
    }

    private void RenderConnections()
    {
        ContentHost.Children.Add(Header("Connections"));
        ContentHost.Children.Add(Card($"Named pipe: {_snapshot.PipeName}"));
        ContentHost.Children.Add(Card($"Agent: {(_snapshot.Connected ? "connected" : "offline")}"));
        ContentHost.Children.Add(Card($"MCP: {(_snapshot.McpSessionPresent ? "active session with mcp clientId" : "not detected — Node MCP server remains the planner gateway")}"));
        ContentHost.Children.Add(Header("Sessions"));
        foreach (var session in _snapshot.Sessions)
        {
            ContentHost.Children.Add(Card($"{session.SessionId} · {session.ClientId} · autoApprove={session.AutoApproveAsk} · grants={session.Grants.Length}"));
        }
    }

    private void RenderLogs()
    {
        ContentHost.Children.Add(Header("Audit logs"));
        foreach (var entry in _snapshot.AuditEntries)
        {
            ContentHost.Children.Add(Card(
                $"[{entry.Id}] {entry.Timestamp}\n{entry.Client}/{entry.SessionId} {entry.Action} success={entry.Success} decision={entry.Decision} {entry.ErrorCode}"));
        }
    }

    private void RenderInspector()
    {
        ContentHost.Children.Add(Header("UIA developer inspector"));
        ContentHost.Children.Add(Card("Queries the agent via Named Pipe (ui.get_tree / ui.find / window.list). No local UIA bypass."));
        _inspectorWindowId = new TextBox { PlaceholderText = "Optional windowId", Width = 260 };
        _inspectorQuery = new TextBox { PlaceholderText = "Name contains (for ui.find)", Width = 260 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var listBtn = new Button { Content = "List windows" };
        listBtn.Click += InspectorListWindows_Click;
        var treeBtn = new Button { Content = "Get tree" };
        treeBtn.Click += InspectorTree_Click;
        var findBtn = new Button { Content = "Find" };
        findBtn.Click += InspectorFind_Click;
        row.Children.Add(_inspectorWindowId);
        row.Children.Add(_inspectorQuery);
        row.Children.Add(listBtn);
        row.Children.Add(treeBtn);
        row.Children.Add(findBtn);
        ContentHost.Children.Add(Wrap(row));
        _inspectorOutput = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 };
        ContentHost.Children.Add(Wrap(_inspectorOutput));
    }

    private void RenderSettings()
    {
        ContentHost.Children.Add(Header("Settings"));
        ContentHost.Children.Add(Card($"Emergency hotkey: {_emergencyHotkey}"));
        ContentHost.Children.Add(Card("UI never executes automation locally — all actions go through the agent permission/execution pipeline."));
        ContentHost.Children.Add(Card("MCP remains TypeScript/Node (official SDK). Migrating MCP to C# is not required for Phase 6."));
        var create = new Button { Content = "Create interactive session (autoApproveAsk=false)" };
        create.Click += CreateSession_Click;
        ContentHost.Children.Add(create);
        var reconnect = new Button { Content = "Ensure agent process" };
        reconnect.Click += async (_, _) =>
        {
            try
            {
                await App.Bridge.EnsureAgentAsync();
                await RefreshAsync(false);
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        };
        ContentHost.Children.Add(reconnect);
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: (string id, string scope) })
        {
            return;
        }

        await App.Bridge.CallAsync(CommandNames.PermissionApprove, new { approvalId = id, scope });
        await RefreshAsync(true);
    }

    private async void Deny_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        await App.Bridge.CallAsync(CommandNames.PermissionDeny, new { approvalId = id });
        await RefreshAsync(true);
    }

    private async void SaveCapability_Click(object sender, RoutedEventArgs e)
    {
        var capability = _capabilityBox?.SelectedItem?.ToString();
        var decision = _capabilityDecisionBox?.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(decision))
        {
            return;
        }

        await App.Bridge.CallAsync(CommandNames.PermissionPolicySet, new { kind = "capability", capability, decision });
        await RefreshAsync(true);
    }

    private async void SaveApp_Click(object sender, RoutedEventArgs e)
    {
        var processName = _appProcessBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        await App.Bridge.CallAsync(CommandNames.PermissionPolicySet, new
        {
            kind = "app",
            processName,
            observe = _appObserveBox?.SelectedItem?.ToString() ?? "Allow",
            interact = _appInteractBox?.SelectedItem?.ToString() ?? "Ask"
        });
        await RefreshAsync(true);
    }

    private async void SavePath_Click(object sender, RoutedEventArgs e)
    {
        var pathPrefix = _pathBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            return;
        }

        await App.Bridge.CallAsync(CommandNames.PermissionPolicySet, new
        {
            kind = "path",
            pathPrefix,
            allowRead = _pathRead?.IsChecked == true,
            allowWrite = _pathWrite?.IsChecked == true,
            deny = _pathDeny?.IsChecked == true
        });
        await RefreshAsync(true);
    }

    private async void CreateSession_Click(object sender, RoutedEventArgs e)
    {
        await App.Bridge.CallAsync(CommandNames.SessionCreate, new { clientId = "control-center", autoApproveAsk = false });
        await RefreshAsync(true);
    }

    private async void InspectorListWindows_Click(object sender, RoutedEventArgs e)
    {
        var result = await App.Bridge.CallAsync(CommandNames.WindowList, new { });
        SetInspectorOutput(result);
    }

    private async void InspectorTree_Click(object sender, RoutedEventArgs e)
    {
        var result = await App.Bridge.CallAsync(CommandNames.UiGetTree, new
        {
            windowId = string.IsNullOrWhiteSpace(_inspectorWindowId?.Text) ? null : _inspectorWindowId!.Text.Trim(),
            depth = 2,
            maxNodes = 80,
            includeBounds = true
        });
        SetInspectorOutput(result);
    }

    private async void InspectorFind_Click(object sender, RoutedEventArgs e)
    {
        var result = await App.Bridge.CallAsync(CommandNames.UiFind, new
        {
            windowId = string.IsNullOrWhiteSpace(_inspectorWindowId?.Text) ? null : _inspectorWindowId!.Text.Trim(),
            selector = new { nameContains = _inspectorQuery?.Text?.Trim() },
            maxResults = 25
        });
        SetInspectorOutput(result);
    }

    private void SetInspectorOutput(JsonElement result)
    {
        if (_inspectorOutput is null)
        {
            return;
        }

        _inspectorOutput.Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static ComboBox DecisionCombo(string selected)
    {
        var box = new ComboBox { Width = 120 };
        box.Items.Add("Allow");
        box.Items.Add("Ask");
        box.Items.Add("Deny");
        box.SelectedItem = selected;
        return box;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 4)
    };

    private static Border Card(string text) => Wrap(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });

    private static Border Wrap(UIElement child) => new()
    {
        Child = child,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 245, 247, 250)),
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(6)
    };
}
