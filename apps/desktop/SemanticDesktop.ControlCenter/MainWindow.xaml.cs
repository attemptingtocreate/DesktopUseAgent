using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Runtime;
using SemanticDesktop.ControlCenter.Client;
using SemanticDesktop.Core.Commands;

namespace SemanticDesktop.ControlCenter;

public sealed partial class MainWindow : Window, IAgentRuntimeObserver
{
    private readonly DispatcherQueueTimer _timer;
    private ControlCenterSnapshot _snapshot = ControlCenterSnapshot.Disconnected(App.Bridge.PipeName);
    private string _section = "chat";
    private string _emergencyHotkey = "Ctrl+Alt+Shift+Esc";
    private string? _conversationId;
    private string? _pendingApprovalId;
    private string? _pendingApprovalDetail;
    private int _actionsCompleted;
    private bool _sending;
    private bool _suppressAgentChange;
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
        App.Observer.Add(this);
        Closed += (_, _) => App.Observer.Remove(this);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += async (_, _) => await RefreshAsync(silent: true);
        _timer.Start();
        _ = BootstrapAsync();
    }

    private async Task BootstrapAsync()
    {
        ReloadConversations();
        ReloadAgents();
        if (_conversationId is null)
        {
            NewChat();
        }

        await RefreshAsync(silent: false);
        RenderChatMessages();
        ApplyStartupSetting();
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
                ? $"Connected · pipe {_snapshot.PipeName}"
                : $"Disconnected · pipe {_snapshot.PipeName}";
            ComputerStatusText.Text = _snapshot.Connected
                ? (_snapshot.EmergencyStopped ? "Computer control: STOPPED" : "Computer control: ready")
                : "Computer control: offline";
            EmergencyButton.Content = _snapshot.EmergencyStopped ? "CLEAR STOP" : "EMERGENCY STOP";
            EmergencyButton.Background = _snapshot.EmergencyStopped
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange)
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 180, 35, 24));

            if (_section == "chat")
            {
                if (_snapshot.Approvals.Count > 0 && ApprovalBar.Visibility != Visibility.Visible)
                {
                    var approval = _snapshot.Approvals[0];
                    ShowApproval(approval.Id, approval.Action, approval.Target ?? approval.Reason);
                }
            }
            else
            {
                RenderSection();
            }
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
            var chat = tag == "chat";
            ChatRoot.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
            PageScroller.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;
            if (chat)
            {
                ReloadConversations();
                ReloadAgents();
                RenderChatMessages();
            }
            else
            {
                RenderSection();
            }
        }
    }

    private void RenderSection()
    {
        ContentHost.Children.Clear();
        switch (_section)
        {
            case "agents":
                RenderAgents();
                break;
            case "mcps":
                RenderMcps();
                break;
            case "activity":
                RenderActivity();
                break;
            case "permissions":
                RenderPermissions();
                break;
            case "settings":
                RenderSettings();
                break;
            case "inspector":
                RenderInspector();
                break;
            case "logs":
                RenderLogs();
                break;
        }
    }

    private void NewChat()
    {
        var convo = App.Host.ConversationsApi.Create();
        _conversationId = convo.Id;
        ReloadConversations();
        RenderChatMessages();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e) => NewChat();

    private void ReloadConversations()
    {
        ConversationList.Items.Clear();
        foreach (var item in App.Host.ConversationsApi.List())
        {
            ConversationList.Items.Add(new ListViewItem
            {
                Content = item.Title,
                Tag = item.Id,
                IsSelected = item.Id == _conversationId
            });
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is ListViewItem { Tag: string id })
        {
            _conversationId = id;
            RenderChatMessages();
            ReloadAgents();
        }
    }

    private void ReloadAgents()
    {
        _suppressAgentChange = true;
        AgentSelector.Items.Clear();
        var providers = App.Host.Providers.List();
        if (providers.Count == 0)
        {
            AgentSelector.Items.Add(new ComboBoxItem { Content = "No agent configured — add one on Agents", Tag = "" });
            AgentSelector.SelectedIndex = 0;
            _suppressAgentChange = false;
            return;
        }

        var selected = _conversationId is null ? null : App.Host.ConversationsApi.Get(_conversationId)?.SelectedProviderId
                       ?? App.Host.Settings.Get().Agents.DefaultProviderId;
        foreach (var provider in providers)
        {
            var label = string.IsNullOrWhiteSpace(provider.DefaultModel)
                ? provider.DisplayName
                : $"{provider.DisplayName} · {provider.DefaultModel}";
            AgentSelector.Items.Add(new ComboBoxItem { Content = label, Tag = provider.Id });
            if (provider.Id == selected)
            {
                AgentSelector.SelectedIndex = AgentSelector.Items.Count - 1;
            }
        }

        if (AgentSelector.SelectedIndex < 0 && AgentSelector.Items.Count > 0)
        {
            AgentSelector.SelectedIndex = 0;
        }

        _suppressAgentChange = false;
    }

    private void AgentSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAgentChange || _conversationId is null)
        {
            return;
        }

        if (AgentSelector.SelectedItem is ComboBoxItem { Tag: string id } && !string.IsNullOrWhiteSpace(id))
        {
            var model = App.Host.Providers.Get(id)?.DefaultModel;
            App.Host.ConversationsApi.SetSelectedProvider(_conversationId, id, model);
        }
    }

    private void RenderChatMessages()
    {
        MessagesHost.Children.Clear();
        if (_conversationId is null)
        {
            MessagesHost.Children.Add(new TextBlock { Text = "Start a conversation to control this computer with an AI agent.", TextWrapping = TextWrapping.Wrap });
            return;
        }

        var convo = App.Host.ConversationsApi.Get(_conversationId);
        if (convo is null || convo.Messages.Count == 0)
        {
            MessagesHost.Children.Add(new TextBlock { Text = "Ask DesktopUseAgent to work on this computer.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            return;
        }

        foreach (var message in convo.Messages)
        {
            MessagesHost.Children.Add(MessageCard(message, convo));
        }

        var turn = convo.Turns.LastOrDefault();
        if (turn?.ToolInvocations.Count > 0)
        {
            foreach (var tool in turn.ToolInvocations)
            {
                if (convo.Messages.Any(m => m.ToolResults?.Any(r => r.ToolCallId == tool.Id) == true))
                {
                    continue;
                }

                MessagesHost.Children.Add(ToolChip(tool));
            }
        }
    }

    private UIElement MessageCard(ChatMessage message, Conversation convo)
    {
        var panel = new StackPanel { Spacing = 6 };
        var who = message.Role == ChatRoles.User ? "You" : "DesktopUseAgent";
        var stamp = message.CreatedAt.ToLocalTime().ToString("t");
        panel.Children.Add(new TextBlock { Text = $"{who} · {stamp}", FontSize = 12, Opacity = 0.65 });
        panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(message.Content) ? "…" : message.Content, TextWrapping = TextWrapping.Wrap });
        if (message.ToolResults is { Count: > 0 })
        {
            foreach (var tool in message.ToolResults)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = tool.Success ? $"[{tool.Summary}]" : $"[Failed: {tool.Summary}]",
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Content = "Copy", Tag = message.Content };
        copy.Click += (_, _) =>
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(message.Content ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        };
        actions.Children.Add(copy);
        if (message.Role == ChatRoles.User && convo.Id == _conversationId)
        {
            var del = new Button { Content = "Delete chat" };
            del.Click += (_, _) =>
            {
                if (_conversationId is not null)
                {
                    App.Host.ConversationsApi.Delete(_conversationId);
                    _conversationId = null;
                    ReloadConversations();
                    NewChat();
                }
            };
            actions.Children.Add(del);
        }

        panel.Children.Add(actions);
        return Wrap(panel, message.Role == ChatRoles.User
            ? "ControlFillColorDefaultBrush"
            : "CardBackgroundFillColorDefaultBrush");
    }

    private static Border ToolChip(ToolInvocation tool) =>
        Wrap(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(tool.Summary) ? $"[{tool.Tool}]" : $"[{tool.Summary}]",
            TextWrapping = TextWrapping.Wrap
        }, "ControlFillColorSecondaryBrush");

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void Composer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        if (_sending)
        {
            return;
        }

        var text = Composer.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_conversationId is null)
        {
            NewChat();
        }

        var providerId = (AgentSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            StatusText.Text = "Configure an agent provider on the Agents page first.";
            return;
        }

        Composer.Text = "";
        _sending = true;
        _actionsCompleted = 0;
        LiveTaskBar.Visibility = Visibility.Visible;
        StopTaskButton.Visibility = Visibility.Visible;
        TaskStatusText.Text = "Working…";
        try
        {
            await App.Host.ConversationsApi.SendAsync(_conversationId!, text, providerId);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _sending = false;
            LiveTaskBar.Visibility = Visibility.Collapsed;
            StopTaskButton.Visibility = Visibility.Collapsed;
            TaskStatusText.Text = "";
            ReloadConversations();
            RenderChatMessages();
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_conversationId is null)
        {
            return;
        }

        try
        {
            await App.Host.ConversationsApi.RetryLastAsync(_conversationId);
            RenderChatMessages();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void StopTask_Click(object sender, RoutedEventArgs e)
    {
        if (_conversationId is not null)
        {
            await App.Host.ConversationsApi.StopAsync(_conversationId, emergency: false);
        }
    }

    public void OnTextDelta(string conversationId, string text) =>
        Dispatch(() =>
        {
            if (conversationId != _conversationId)
            {
                return;
            }

            TaskStatusText.Text = "Responding…";
        });

    public void OnToolStarted(string conversationId, ToolInvocation invocation) =>
        Dispatch(() =>
        {
            if (conversationId != _conversationId)
            {
                return;
            }

            LiveTaskBar.Visibility = Visibility.Visible;
            StopTaskButton.Visibility = Visibility.Visible;
            CurrentActionText.Text = $"Current action: {invocation.Summary ?? invocation.Tool}";
            MessagesHost.Children.Add(ToolChip(invocation));
        });

    public void OnToolCompleted(string conversationId, ToolInvocation invocation) =>
        Dispatch(() =>
        {
            if (conversationId != _conversationId)
            {
                return;
            }

            _actionsCompleted++;
            ActionsCompletedText.Text = $"Actions completed: {_actionsCompleted}";
            CurrentActionText.Text = $"Current action: {invocation.Summary ?? invocation.Tool}";
        });

    public void OnApprovalNeeded(string conversationId, string approvalId, string action, string? target) =>
        Dispatch(() => ShowApproval(approvalId, action, target));

    public void OnStatus(string conversationId, string status) =>
        Dispatch(() =>
        {
            TaskStatusText.Text = status == TurnStatus.Streaming ? "Working…" : "";
            if (status is TurnStatus.Completed or TurnStatus.Cancelled or TurnStatus.Error or TurnStatus.PermissionDenied)
            {
                LiveTaskBar.Visibility = Visibility.Collapsed;
                StopTaskButton.Visibility = Visibility.Collapsed;
            }
        });

    private void ShowApproval(string approvalId, string action, string? target)
    {
        _pendingApprovalId = approvalId;
        _pendingApprovalDetail = action + (string.IsNullOrWhiteSpace(target) ? "" : "\n" + target);
        ApprovalText.Text = "DesktopUseAgent wants to run an action.";
        ApprovalDetail.Text = _pendingApprovalDetail;
        ApprovalBar.Visibility = Visibility.Visible;
    }

    private async void AllowChatApproval_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingApprovalId is null)
        {
            return;
        }

        await App.Bridge.CallAsync(CommandNames.PermissionApprove, new { approvalId = _pendingApprovalId, scope = "once" });
        ApprovalBar.Visibility = Visibility.Collapsed;
        _pendingApprovalId = null;
    }

    private async void DenyChatApproval_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingApprovalId is null)
        {
            return;
        }

        await App.Bridge.CallAsync(CommandNames.PermissionDeny, new { approvalId = _pendingApprovalId });
        ApprovalBar.Visibility = Visibility.Collapsed;
        _pendingApprovalId = null;
    }

    private async void ViewApproval_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Requested action",
            Content = _pendingApprovalDetail ?? "",
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void Dispatch(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        DispatcherQueue.TryEnqueue(() => action());
    }

    private void RenderAgents()
    {
        ContentHost.Children.Add(Header("Agents"));
        ContentHost.Children.Add(Card("Only supported providers are listed. Website session/cookie integrations are not available."));
        foreach (var provider in App.Host.Providers.List())
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = provider.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"{provider.Type} · model {provider.DefaultModel ?? "—"} · MCPs {string.Join(", ", provider.EnabledMcpIds)}" });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var test = new Button { Content = "Test Connection", Tag = provider.Id };
            test.Click += async (_, _) =>
            {
                try
                {
                    var impl = App.Host.ProviderResolver.Resolve(provider.Id);
                    var status = await impl.ConnectAsync();
                    StatusText.Text = $"{provider.DisplayName}: {status.Kind} {status.Message}";
                }
                catch (Exception ex)
                {
                    StatusText.Text = ex.Message;
                }
            };
            var disconnect = new Button { Content = "Disconnect", Tag = provider.Id };
            disconnect.Click += (_, _) =>
            {
                App.Host.Providers.Remove(provider.Id);
                ReloadAgents();
                RenderSection();
            };
            row.Children.Add(test);
            row.Children.Add(disconnect);
            panel.Children.Add(row);
            ContentHost.Children.Add(Wrap(panel));
        }

        ContentHost.Children.Add(Header("Add agent"));
        var typeBox = new ComboBox { Width = 220 };
        typeBox.Items.Add("openai");
        typeBox.Items.Add("anthropic");
        typeBox.Items.Add("ollama");
        typeBox.SelectedIndex = 0;
        var nameBox = new TextBox { PlaceholderText = "Display name", Width = 200 };
        var modelBox = new TextBox { PlaceholderText = "Default model", Width = 180 };
        var keyBox = new PasswordBox { PlaceholderText = "API key (stored locally)", Width = 240 };
        var urlBox = new TextBox { PlaceholderText = "Base URL (optional)", Width = 240 };
        var add = new Button { Content = "Add Agent" };
        add.Click += (_, _) =>
        {
            var type = typeBox.SelectedItem?.ToString() ?? "openai";
            var config = new ProviderConfig
            {
                Type = type,
                DisplayName = string.IsNullOrWhiteSpace(nameBox.Text) ? type : nameBox.Text.Trim(),
                DefaultModel = string.IsNullOrWhiteSpace(modelBox.Text) ? null : modelBox.Text.Trim(),
                BaseUrl = string.IsNullOrWhiteSpace(urlBox.Text) ? null : urlBox.Text.Trim(),
                EnabledMcpIds = new List<string> { McpIds.Builtin }
            };
            var saved = App.Host.Providers.Upsert(config);
            if (!string.IsNullOrWhiteSpace(keyBox.Password))
            {
                App.Host.Credentials.Set(CredentialStore.ProviderApiKey(saved.Id), keyBox.Password);
            }

            var settings = App.Host.Settings.Get();
            settings.Agents.DefaultProviderId ??= saved.Id;
            settings.Agents.DefaultModel ??= saved.DefaultModel;
            App.Host.Settings.Save(settings);
            ReloadAgents();
            RenderSection();
        };
        ContentHost.Children.Add(Wrap(typeBox));
        ContentHost.Children.Add(Wrap(nameBox));
        ContentHost.Children.Add(Wrap(modelBox));
        ContentHost.Children.Add(Wrap(urlBox));
        ContentHost.Children.Add(Wrap(keyBox));
        ContentHost.Children.Add(add);
    }

    private void RenderMcps()
    {
        ContentHost.Children.Add(Header("MCP Servers"));
        foreach (var mcp in App.Host.Mcp.List())
        {
            var status = App.Host.Mcp.GetStatus(mcp.Id);
            var panel = new StackPanel { Spacing = 6 };
            var kind = mcp.IsBuiltin ? "Built-in" : mcp.Type;
            panel.Children.Add(new TextBlock { Text = $"{mcp.Name}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"{kind} · {status.State} · {status.ToolCount} tools{(status.Error is null ? "" : " · " + status.Error)}" });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var enabled = new CheckBox { Content = "Enabled", IsChecked = mcp.Enabled, Tag = mcp.Id };
            enabled.Click += (_, _) =>
            {
                App.Host.Mcp.Enable(mcp.Id, enabled.IsChecked == true);
                RenderSection();
            };
            var test = new Button { Content = "Test Connection", Tag = mcp.Id };
            test.Click += async (_, _) =>
            {
                var result = await App.Host.Mcp.TestConnectionAsync(mcp.Id);
                StatusText.Text = $"{result.Name}: {result.State} ({result.ToolCount} tools)";
                RenderSection();
            };
            row.Children.Add(enabled);
            row.Children.Add(test);
            if (!mcp.IsBuiltin)
            {
                var remove = new Button { Content = "Remove", Tag = mcp.Id };
                remove.Click += (_, _) =>
                {
                    App.Host.Mcp.Remove(mcp.Id);
                    RenderSection();
                };
                row.Children.Add(remove);
            }

            panel.Children.Add(row);
            ContentHost.Children.Add(Wrap(panel));
        }

        ContentHost.Children.Add(Header("Add MCP"));
        var name = new TextBox { PlaceholderText = "Name", Width = 200 };
        var command = new TextBox { PlaceholderText = "Command (e.g. npx)", Width = 200 };
        var args = new TextBox { PlaceholderText = "Args (space-separated)", Width = 280 };
        var add = new Button { Content = "+ Add MCP" };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(command.Text))
            {
                return;
            }

            App.Host.Mcp.Add(new McpConfiguration
            {
                Name = string.IsNullOrWhiteSpace(name.Text) ? command.Text.Trim() : name.Text.Trim(),
                Type = "stdio",
                Command = command.Text.Trim(),
                Args = string.IsNullOrWhiteSpace(args.Text) ? new List<string>() : args.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList()
            }, App.Host.Settings.Get().Agents.DefaultProviderId);
            RenderSection();
        };
        ContentHost.Children.Add(Wrap(name));
        ContentHost.Children.Add(Wrap(command));
        ContentHost.Children.Add(Wrap(args));
        ContentHost.Children.Add(add);

        ContentHost.Children.Add(Header("Per-agent MCP assignment"));
        foreach (var provider in App.Host.Providers.List())
        {
            var box = new StackPanel { Spacing = 4 };
            box.Children.Add(new TextBlock { Text = provider.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var mcp in App.Host.Mcp.List())
            {
                var check = new CheckBox
                {
                    Content = mcp.Name,
                    IsChecked = provider.EnabledMcpIds.Contains(mcp.Id, StringComparer.OrdinalIgnoreCase)
                };
                var providerId = provider.Id;
                var mcpId = mcp.Id;
                check.Click += (_, _) =>
                {
                    App.Host.Providers.AssignMcp(providerId, mcpId, check.IsChecked == true);
                };
                box.Children.Add(check);
            }

            ContentHost.Children.Add(Wrap(box));
        }
    }

    private void RenderActivity()
    {
        ContentHost.Children.Add(Header("Activity"));
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
            ContentHost.Children.Add(Card("No pending approvals."));
        }
        else
        {
            foreach (var approval in _snapshot.Approvals)
            {
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(new TextBlock
                {
                    Text = $"DesktopUseAgent wants to run {approval.Action}.\n{approval.Capability} · risk {approval.Risk}\n{approval.Reason}\n{approval.Target}",
                    TextWrapping = TextWrapping.Wrap
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
        ContentHost.Children.Add(Header("Developer inspector"));
        ContentHost.Children.Add(Card("Queries the agent via named pipe (ui.get_tree / ui.find / window.list). No local UIA bypass."));
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
        var settings = App.Host.Settings.Get();
        ContentHost.Children.Add(Header("General"));
        var launch = Toggle("Launch at startup", settings.General.LaunchAtStartup, value =>
        {
            settings.General.LaunchAtStartup = value;
            App.Host.Settings.Save(settings);
            ApplyStartupSetting();
        });
        var tray = Toggle("Minimize to tray (keep process on close)", settings.General.MinimizeToTray, value =>
        {
            settings.General.MinimizeToTray = value;
            App.Host.Settings.Save(settings);
        });
        var notify = Toggle("Notifications", settings.General.Notifications, value =>
        {
            settings.General.Notifications = value;
            App.Host.Settings.Save(settings);
        });
        ContentHost.Children.Add(launch);
        ContentHost.Children.Add(tray);
        ContentHost.Children.Add(notify);
        var close = new ComboBox { Width = 280 };
        close.Items.Add("Leave agent running");
        close.Items.Add("Exit everything");
        close.SelectedIndex = settings.General.CloseBehavior == CloseBehavior.ExitAll ? 1 : 0;
        close.SelectionChanged += (_, _) =>
        {
            settings.General.CloseBehavior = close.SelectedIndex == 1 ? CloseBehavior.ExitAll : CloseBehavior.LeaveAgentRunning;
            App.Host.Settings.Save(settings);
        };
        ContentHost.Children.Add(Card("Close behavior"));
        ContentHost.Children.Add(Wrap(close));

        ContentHost.Children.Add(Header("Computer control"));
        ContentHost.Children.Add(Toggle("Enabled", settings.ComputerControl.Enabled, value =>
        {
            settings.ComputerControl.Enabled = value;
            App.Host.Settings.Save(settings);
        }));
        ContentHost.Children.Add(Card($"Emergency stop shortcut: {_emergencyHotkey}"));
        ContentHost.Children.Add(Toggle("Prefer semantic interfaces over fallback input/vision", settings.ComputerControl.PreferSemanticOverFallback, value =>
        {
            settings.ComputerControl.PreferSemanticOverFallback = value;
            App.Host.Settings.Save(settings);
        }));

        ContentHost.Children.Add(Header("Privacy"));
        ContentHost.Children.Add(Card($"Conversation retention: {settings.Privacy.ConversationRetentionDays} days (local only)"));
        ContentHost.Children.Add(Toggle("Local logs", settings.Privacy.LocalLogs, value =>
        {
            settings.Privacy.LocalLogs = value;
            App.Host.Settings.Save(settings);
        }));
        ContentHost.Children.Add(Toggle("Telemetry", settings.Privacy.TelemetryEnabled, value =>
        {
            settings.Privacy.TelemetryEnabled = value;
            App.Host.Settings.Save(settings);
        }));

        ContentHost.Children.Add(Header("Advanced"));
        ContentHost.Children.Add(Card($"Agent: {(_snapshot.Connected ? "running" : "offline")} · pipe {_snapshot.PipeName}"));
        ContentHost.Children.Add(Card($"MCP gateway (Mode B): {(App.Host.McpGatewayStatus.AvailableAsStdioModule ? App.Host.McpGatewayStatus.Path : "not found")}"));
        ContentHost.Children.Add(Card(App.Host.McpGatewayStatus.Note));
        ContentHost.Children.Add(Card($"Data root: {App.Host.DataRoot}"));
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
        var session = new Button { Content = "Create interactive session (autoApproveAsk=false)" };
        session.Click += CreateSession_Click;
        ContentHost.Children.Add(session);
    }

    private static Border Toggle(string label, bool initial, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, IsChecked = initial };
        box.Click += (_, _) => changed(box.IsChecked == true);
        return Wrap(box);
    }

    private static void ApplyStartupSetting()
    {
        try
        {
            var enabled = App.Host.Settings.Get().General.LaunchAtStartup;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            const string name = "DesktopUseAgent";
            if (enabled)
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "DesktopUseAgent.exe");
                key?.SetValue(name, $"\"{exe}\"");
            }
            else
            {
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
        }
        catch
        {
            // startup registration is best-effort
        }
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
        await App.Bridge.CallAsync(CommandNames.SessionCreate, new { clientId = "desktopuseagent-chat", autoApproveAsk = false, approvalTimeoutSeconds = 600 });
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

    private static Border Wrap(UIElement child) => Wrap(child, "CardBackgroundFillColorDefaultBrush");

    private static Border Wrap(UIElement child, string backgroundResource) => new()
    {
        Child = child,
        Background = ThemeBrush(backgroundResource),
        BorderBrush = ThemeBrush("ControlStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(8)
    };

    private static Brush ThemeBrush(string resourceKey) =>
        (Brush)Application.Current.Resources[resourceKey];
}
