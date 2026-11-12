using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTimeCompanion.App.Services;

namespace ScreenTimeCompanion.App.Views;

public sealed class WidgetWindow : Window
{
    private readonly ITrackerService _service;
    private readonly TextBlock _total = UiStyles.Text("0:00:00", 34, weight: FontWeights.SemiBold);
    private readonly TextBlock _status = UiStyles.Text("准备记录", 11, UiStyles.Green);
    private readonly TextBlock _current = UiStyles.Text("", 11, UiStyles.Muted);
    private readonly TextBlock _heading = UiStyles.Text("今日有效使用", 11, UiStyles.Muted);
    private readonly TextBlock _other = UiStyles.Text("暂无使用记录", 10, UiStyles.Muted);
    private readonly StackPanel _apps = new();
    private readonly Button _pause = UiStyles.Button("暂停");
    private readonly Button _pin = UiStyles.Button("置顶");
    private bool _allowClose;
    private bool _busy;
    private bool _sessionMetric;

    public WidgetWindow(ITrackerService service)
    {
        _service = service;
        Title = "屏幕时光";
        Width = 280;
        Height = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        UseLayoutRounding = true;
        Content = BuildContent();

        var settings = service.State.Settings;
        Topmost = settings.AlwaysOnTop;
        Left = settings.WidgetLeft ?? SystemParameters.WorkArea.Right - Width - 24;
        Top = settings.WidgetTop ?? SystemParameters.WorkArea.Top + 80;
        EnsureVisiblePosition();

        _service.Changed += Render;
        Closing += OnClosing;
        Closed += (_, _) => _service.Changed -= Render;
        Loaded += (_, _) => EnsureVisiblePosition();
        Render();
    }

    private UIElement BuildContent()
    {
        var surface = new Border
        {
            Background = UiStyles.Panel,
            BorderBrush = UiStyles.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(16)
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(105) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = UiStyles.Text("⠿  屏幕时光", 14, weight: FontWeights.SemiBold);
        title.Cursor = Cursors.SizeAll;
        title.ToolTip = "拖动移动小窗；右键选择统计口径";
        title.MouseLeftButtonDown += async (_, args) =>
        {
            if (args.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            EnsureVisiblePosition();
            await ExecuteAsync(() => _service.SaveWindowPositionAsync(Left, Top));
        };
        var metricMenu = new ContextMenu();
        var activeMenu = new MenuItem { Header = "显示有效使用时长", IsCheckable = true, IsChecked = true };
        var sessionMenu = new MenuItem { Header = "显示屏幕会话时长", IsCheckable = true };
        activeMenu.Click += (_, _) => { _sessionMetric = false; activeMenu.IsChecked = true; sessionMenu.IsChecked = false; Render(); };
        sessionMenu.Click += (_, _) => { _sessionMetric = true; activeMenu.IsChecked = false; sessionMenu.IsChecked = true; Render(); };
        metricMenu.Items.Add(activeMenu);
        metricMenu.Items.Add(sessionMenu);
        title.ContextMenu = metricMenu;
        header.Children.Add(title);

        _pin.Padding = new Thickness(7, 3, 7, 3);
        _pin.MinHeight = 26;
        _pin.Margin = new Thickness(0, 0, 5, 0);
        _pin.Click += async (_, _) => await ExecuteAsync(() => _service.SetAlwaysOnTopAsync(!_service.State.Settings.AlwaysOnTop));
        _pin.ToolTip = "切换小窗始终置顶";
        Grid.SetColumn(_pin, 1);
        header.Children.Add(_pin);
        var close = UiStyles.Button("×");
        close.Padding = new Thickness(6, 2, 6, 2);
        close.MinHeight = 26;
        close.BorderThickness = new Thickness(0);
        close.ToolTip = "隐藏小窗，保持当前记录状态";
        AutomationProperties.SetName(close, "隐藏桌面小窗");
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        layout.Children.Add(header);

        var summary = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        summary.Children.Add(_heading);
        _total.Margin = new Thickness(0, 1, 0, 0);
        summary.Children.Add(_total);
        summary.Children.Add(_status);
        _current.Margin = new Thickness(0, 3, 0, 0);
        summary.Children.Add(_current);
        Grid.SetRow(summary, 1);
        layout.Children.Add(summary);

        var listArea = new Border
        {
            BorderBrush = UiStyles.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 5),
            Margin = new Thickness(0, 6, 0, 6)
        };
        var list = new DockPanel();
        _other.Margin = new Thickness(0, 5, 0, 0);
        DockPanel.SetDock(_other, Dock.Bottom);
        list.Children.Add(_other);
        list.Children.Add(_apps);
        listArea.Child = list;
        Grid.SetRow(listArea, 2);
        layout.Children.Add(listArea);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        _pause.Margin = new Thickness(0, 0, 5, 0);
        _pause.Click += async (_, _) => await ExecuteAsync(() => _service.SetPausedAsync(!_service.State.Settings.Paused));
        footer.Children.Add(_pause);
        var details = UiStyles.Button("查看详情  ↗", true);
        details.Margin = new Thickness(5, 0, 0, 0);
        details.Click += (_, _) => _service.ShowDashboard();
        Grid.SetColumn(details, 1);
        footer.Children.Add(details);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        surface.Child = layout;
        return surface;
    }

    private void Render()
    {
        var state = _service.State;
        var rows = UiStyles.Aggregate(state, 1, _sessionMetric);
        var total = rows.Sum(row => row.Milliseconds) / 1000;
        _heading.Text = _sessionMetric ? "今日屏幕会话" : "今日有效使用";
        _total.Text = $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}";
        _total.ToolTip = "小时 : 分钟 : 秒";
        _status.Text = "● " + state.Status;
        _status.Foreground = state.Healthy && !state.Settings.Paused ? UiStyles.Green : UiStyles.Warning;
        _status.ToolTip = state.Status;
        _current.Text = state.Settings.Paused ? "恢复记录后继续统计" : "当前：" + state.CurrentApp;
        _current.ToolTip = state.CurrentApp;
        _pause.Content = state.Settings.Paused ? "恢复记录" : "暂停记录";
        Topmost = state.Settings.AlwaysOnTop;
        _pin.Background = Topmost ? UiStyles.Mint : UiStyles.Panel;
        _pin.Foreground = Topmost ? UiStyles.Green : UiStyles.Muted;
        _apps.Children.Clear();
        foreach (var row in rows.Take(3))
        {
            var item = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            item.ColumnDefinitions.Add(new ColumnDefinition());
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = UiStyles.Text(row.Name, 11);
            name.Margin = new Thickness(0, 0, 8, 0);
            name.ToolTip = row.Name;
            item.Children.Add(name);
            var duration = UiStyles.Text(UiStyles.CompactDuration(row.Milliseconds), 11, UiStyles.Muted);
            Grid.SetColumn(duration, 1);
            item.Children.Add(duration);
            _apps.Children.Add(item);
        }
        var remaining = rows.Skip(3).Sum(row => row.Milliseconds);
        _other.Text = rows.Count == 0 ? "暂无使用记录，开始使用电脑即可" : remaining > 0 ? "其他应用  " + UiStyles.CompactDuration(remaining) : $"共 {rows.Count} 个应用";
    }

    private void EnsureVisiblePosition()
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || Left + 40 < virtualLeft || Left > virtualRight - 40 || Top < virtualTop || Top > virtualBottom - 40)
        {
            Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width - 24);
            Top = SystemParameters.WorkArea.Top + 40;
        }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception exception) { UiStyles.ShowError(this, exception); }
        finally { _busy = false; }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        Hide();
    }

    public void ShowSafely()
    {
        EnsureVisiblePosition();
        Show();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _service.Changed -= Render;
        Close();
    }
}
