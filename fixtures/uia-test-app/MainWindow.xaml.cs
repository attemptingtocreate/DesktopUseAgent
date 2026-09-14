using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UiaTestApp;

public partial class MainWindow : Window
{
    private int _dynamicCount;

    public MainWindow()
    {
        InitializeComponent();
        ColorCombo.SelectedIndex = 0;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        ResultText.Text = "Result: primary-clicked";
        StatusText.Text = "Primary clicked";
    }

    private void NestedAction_Click(object sender, RoutedEventArgs e)
    {
        ResultText.Text = "Result: nested-clicked";
        StatusText.Text = "Nested clicked";
    }

    private void AddDynamic_Click(object sender, RoutedEventArgs e)
    {
        _dynamicCount++;
        var button = new Button
        {
            Content = $"Dynamic {_dynamicCount}",
            Margin = new Thickness(0, 0, 0, 4),
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"DynamicButton{_dynamicCount}");
        button.Click += (_, _) =>
        {
            ResultText.Text = $"Result: dynamic-{_dynamicCount}";
        };
        DynamicHost.Children.Add(button);
        StatusText.Text = "Dynamic control added";
    }

    private void ShowDelayed_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Waiting for delayed control...";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DelayedHost.Children.Clear();
            var button = new Button
            {
                Content = "Delayed Ready",
                Width = 140,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "DelayedButton");
            button.Click += (_, _) => ResultText.Text = "Result: delayed-clicked";
            DelayedHost.Children.Add(button);
            StatusText.Text = "Delayed control ready";
        };
        timer.Start();
    }

    private void OpenDialog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Fixture Dialog",
            Width = 320,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(dialog, "FixtureDialog");

        var panel = new StackPanel { Margin = new Thickness(16) };
        var label = new TextBlock { Text = "Dialog open", Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "DialogOkButton");
        ok.Click += (_, _) => dialog.Close();
        panel.Children.Add(label);
        panel.Children.Add(ok);
        dialog.Content = panel;
        dialog.ShowDialog();
        StatusText.Text = "Dialog closed";
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
