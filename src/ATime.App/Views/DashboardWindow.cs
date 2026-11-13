using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ATime.App.Services;

namespace ATime.App.Views;

public sealed class DashboardWindow : Window
{
    private readonly ITrackerService _service;
    private readonly ComboBox _range = new() { Width = 110, MinHeight = 32 };
    private readonly ComboBox _metric = new() { Width = 140, MinHeight = 32 };
    private readonly ComboBox _threshold = new() { Width = 100, MinHeight = 30 };
    private readonly CheckBox _topmost = new() { Content = "小窗始终置顶", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _date = UiStyles.Text("", 12, UiStyles.Muted);
    private readonly TextBlock _total = UiStyles.Text("0 秒", 30, weight: FontWeights.SemiBold);
    private readonly TextBlock _idle = UiStyles.Text("0 秒", 25, weight: FontWeights.SemiBold);
    private readonly TextBlock _current = UiStyles.Text("", 19, weight: FontWeights.SemiBold);
    private readonly TextBlock _totalLabel = UiStyles.Text("今日有效使用", 12, UiStyles.Muted);
    private readonly TextBlock _status = UiStyles.Text("", 12, UiStyles.Green);
    private readonly TextBlock _saved = UiStyles.Text("", 10, UiStyles.Muted);
    private readonly TextBlock _empty = UiStyles.Text("暂无使用记录。保持程序运行，开始使用电脑后这里会自动更新。", 13, UiStyles.Muted);
    private readonly Button _pause = UiStyles.Button("暂停记录");
    private readonly Button _export = UiStyles.Button("导出 CSV");
    private readonly Button _clear = UiStyles.Button("清空历史");
    private readonly ObservableCollection<UsageItem> _items = new();
    private bool _allowClose;
    private bool _rendering;
    private bool _busy;
    private int SelectedDays => _range.SelectedIndex switch { 1 => 7, 2 => 30, _ => 1 };
    private bool SessionMetric => _metric.SelectedIndex == 1;

    public DashboardWindow(ITrackerService service)
    {
        _service = service;
        Title = "ATime · 使用概览";
        Icon = Branding.WindowIcon;
        Width = 1000;
        Height = 720;
        MinWidth = 760;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = UiStyles.Paper;
        Foreground = UiStyles.Ink;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Content = BuildContent();
        _service.Changed += Render;
        Closing += OnClosing;
        Closed += (_, _) => _service.Changed -= Render;
        Render();
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(28, 22, 28, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var widget = UiStyles.Button("显示小窗");
        widget.Margin = new Thickness(0, 0, 10, 0);
        widget.Click += (_, _) => _service.ShowWidget();
        tools.Children.Add(widget);
        _pause.Click += async (_, _) => await ExecuteAsync(() => _service.SetPausedAsync(!_service.State.Settings.Paused));
        tools.Children.Add(_pause);
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        var title = new StackPanel();
        title.Children.Add(UiStyles.Text("ATime", 25, weight: FontWeights.SemiBold));
        _date.Margin = new Thickness(0, 5, 0, 0);
        title.Children.Add(_date);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var logo = UiStyles.Logo(42);
        logo.Margin = new Thickness(0, 0, 12, 0);
        brand.Children.Add(logo);
        brand.Children.Add(title);
        header.Children.Add(brand);
        root.Children.Add(header);

        var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var filterControls = new StackPanel { Orientation = Orientation.Horizontal };
        _range.Items.Add("今日");
        _range.Items.Add("近 7 日");
        _range.Items.Add("近 30 日");
        _range.SelectedIndex = 0;
        _range.Margin = new Thickness(0, 0, 10, 0);
        _range.SelectionChanged += (_, _) => { if (!_rendering) Render(); };
        _metric.Items.Add("有效使用时长");
        _metric.Items.Add("屏幕会话时长");
        _metric.SelectedIndex = 0;
        _metric.SelectionChanged += (_, _) => { if (!_rendering) Render(); };
        filterControls.Children.Add(_range);
        filterControls.Children.Add(_metric);
        filters.Children.Add(filterControls);
        var hint = UiStyles.Text("本地记录 · 不采集屏幕或输入内容", 11, UiStyles.Muted);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        filters.Children.Add(hint);
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        var summaries = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        for (var index = 0; index < 3; index++) summaries.ColumnDefinitions.Add(new ColumnDefinition());
        summaries.Children.Add(SummaryCard(_totalLabel, _total, "各应用使用时间之和", 0));
        summaries.Children.Add(SummaryCard(UiStyles.Text("空闲时长", 12, UiStyles.Muted), _idle, "屏幕会话减去有效使用", 1));
        summaries.Children.Add(SummaryCard(UiStyles.Text("当前前台应用", 12, UiStyles.Muted), _current, "后台运行的应用不累计使用时长", 2));
        Grid.SetRow(summaries, 2);
        root.Children.Add(summaries);

        var appPanel = new Border
        {
            Background = UiStyles.Panel, BorderBrush = UiStyles.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 18, 12)
        };
        var appLayout = new Grid();
        appLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        appLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var appHeading = UiStyles.Text("应用使用时间", 15, weight: FontWeights.SemiBold);
        appHeading.Margin = new Thickness(0, 0, 0, 12);
        appLayout.Children.Add(appHeading);
        var table = new DataGrid
        {
            ItemsSource = _items, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            CanUserDeleteRows = false, CanUserReorderColumns = false, CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None,
            RowHeaderWidth = 0, RowHeight = 40, ColumnHeaderHeight = 34, BorderThickness = new Thickness(0),
            Background = UiStyles.Panel, Foreground = UiStyles.Ink, AlternatingRowBackground = UiStyles.Paper,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        table.Columns.Add(new DataGridTextColumn { Header = "应用", Binding = new Binding(nameof(UsageItem.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180 });
        table.Columns.Add(new DataGridTextColumn { Header = "使用时长", Binding = new Binding(nameof(UsageItem.Duration)), Width = 185 });
        table.Columns.Add(new DataGridTextColumn { Header = "占比", Binding = new Binding(nameof(UsageItem.Share)), Width = 85 });
        Grid.SetRow(table, 1);
        appLayout.Children.Add(table);
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Margin = new Thickness(20, 36, 20, 0);
        _empty.IsHitTestVisible = false;
        Grid.SetRow(_empty, 1);
        appLayout.Children.Add(_empty);
        appPanel.Child = appLayout;
        Grid.SetRow(appPanel, 3);
        root.Children.Add(appPanel);

        var settings = new DockPanel { Margin = new Thickness(0, 18, 0, 15) };
        var rightActions = new StackPanel { Orientation = Orientation.Horizontal };
        _export.Margin = new Thickness(0, 0, 8, 0);
        _export.Click += async (_, _) => await ExportAsync();
        _clear.Click += async (_, _) => await ClearAsync();
        rightActions.Children.Add(_export);
        rightActions.Children.Add(_clear);
        DockPanel.SetDock(rightActions, Dock.Right);
        settings.Children.Add(rightActions);
        var settingsControls = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var thresholdLabel = UiStyles.Text("空闲判定", 12, UiStyles.Muted);
        thresholdLabel.Margin = new Thickness(0, 0, 8, 0);
        settingsControls.Children.Add(thresholdLabel);
        foreach (var minutes in new[] { 1, 3, 5, 10, 15 })
            _threshold.Items.Add(new ComboBoxItem { Content = $"{minutes} 分钟", Tag = minutes * 60 });
        _threshold.SelectionChanged += async (_, _) =>
        {
            if (!_rendering && _threshold.SelectedItem is ComboBoxItem { Tag: int seconds })
                await ExecuteAsync(() => _service.SetIdleThresholdAsync(seconds));
        };
        settingsControls.Children.Add(_threshold);
        _topmost.Margin = new Thickness(20, 0, 15, 0);
        _topmost.Click += async (_, _) =>
        {
            if (!_rendering) await ExecuteAsync(() => _service.SetAlwaysOnTopAsync(_topmost.IsChecked == true));
        };
        settingsControls.Children.Add(_topmost);
        settings.Children.Add(settingsControls);
        Grid.SetRow(settings, 4);
        root.Children.Add(settings);

        var footer = new StackPanel();
        footer.Children.Add(_status);
        _saved.Margin = new Thickness(0, 5, 0, 0);
        _saved.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_saved);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);
        return root;
    }

    private static Border SummaryCard(TextBlock label, TextBlock value, string hint, int column)
    {
        var panel = new StackPanel();
        panel.Children.Add(label);
        value.Margin = new Thickness(0, 8, 0, 7);
        value.Height = 43;
        panel.Children.Add(value);
        panel.Children.Add(UiStyles.Text(hint, 10, UiStyles.Muted));
        var card = new Border
        {
            Background = UiStyles.Panel, BorderBrush = UiStyles.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(17), Child = panel,
            Margin = new Thickness(column == 0 ? 0 : 7, 0, column == 2 ? 0 : 7, 0)
        };
        Grid.SetColumn(card, column);
        return card;
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            var state = _service.State;
            var rows = UiStyles.Aggregate(state, SelectedDays, SessionMetric);
            var effective = UiStyles.Aggregate(state, SelectedDays, false).Sum(row => row.Milliseconds);
            var session = UiStyles.Aggregate(state, SelectedDays, true).Sum(row => row.Milliseconds);
            var total = rows.Sum(row => row.Milliseconds);
            _totalLabel.Text = (SelectedDays == 1 ? "今日" : $"近 {SelectedDays} 日") + (SessionMetric ? "屏幕会话" : "有效使用");
            _date.Text = SelectedDays == 1 ? state.Today.ToString("yyyy 年 M 月 d 日", CultureInfo.InvariantCulture) : $"{state.Today.AddDays(1 - SelectedDays):yyyy-MM-dd} — {state.Today:yyyy-MM-dd}";
            _date.ToolTip = "统计时区：" + state.Settings.ReportingTimeZoneId;
            _total.Text = UiStyles.Duration(total);
            _total.ToolTip = _total.Text;
            _idle.Text = UiStyles.Duration(Math.Max(0, session - effective));
            _idle.ToolTip = _idle.Text;
            _current.Text = state.Settings.Paused ? "已暂停" : state.CurrentApp;
            _current.ToolTip = state.CurrentApp;
            _status.Text = "● " + state.Status;
            _status.Foreground = state.Healthy && !state.Settings.Paused ? UiStyles.Green : UiStyles.Warning;
            _status.ToolTip = state.Status;
            _pause.Content = state.Settings.Paused ? "恢复记录" : "暂停记录";
            _topmost.IsChecked = state.Settings.AlwaysOnTop;
            foreach (ComboBoxItem item in _threshold.Items)
                if (item.Tag is int seconds && seconds == state.Settings.IdleThresholdSeconds) _threshold.SelectedItem = item;
            var saved = state.LastSavedUtc is { } last ? last.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "尚未保存";
            _saved.Text = $"最近保存：{saved}  ·  采集中断：{state.GapCount} 次  ·  关闭窗口后仍在托盘运行  ·  阈值修改仅影响之后的统计";
            UpdateRows(rows, total);
            _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _rendering = false; }
    }

    private void UpdateRows(IReadOnlyList<UiStyles.AppTotal> rows, long total)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var existing = _items.FirstOrDefault(item => item.Id == row.Id);
            if (existing is null)
            {
                existing = new UsageItem(row.Id);
                _items.Insert(index, existing);
            }
            else
            {
                var oldIndex = _items.IndexOf(existing);
                if (oldIndex != index) _items.Move(oldIndex, index);
            }
            existing.Update(row.Name, UiStyles.Duration(row.Milliseconds), total == 0 ? "0.0%" : ((double)row.Milliseconds / total).ToString("P1", CultureInfo.CurrentCulture));
        }
        while (_items.Count > rows.Count) _items.RemoveAt(_items.Count - 1);
    }

    private async Task ExportAsync()
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出应用使用时长", Filter = "CSV 文件 (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true,
            FileName = $"screen-time-{_service.State.Today:yyyy-MM-dd}-{SelectedDays}d-{(SessionMetric ? "session" : "effective")}.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true) return;
        await ExecuteAsync(async () =>
        {
            await _service.ExportCsvAsync(dialog.FileName, SelectedDays, SessionMetric);
            MessageBox.Show(this, "已导出到：\n" + dialog.FileName, "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async Task ClearAsync()
    {
        if (_busy) return;
        if (MessageBox.Show(this, "将删除所有历史使用记录，并保留当前设置。此操作不能撤销。\n\n确定清空历史记录吗？", "清空历史记录",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ExecuteAsync(() => _service.ClearHistoryAsync());
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _pause.IsEnabled = _export.IsEnabled = _clear.IsEnabled = _threshold.IsEnabled = _topmost.IsEnabled = false;
        try { await action(); }
        catch (Exception exception) { UiStyles.ShowError(this, exception); }
        finally
        {
            _busy = false;
            _pause.IsEnabled = _export.IsEnabled = _clear.IsEnabled = _threshold.IsEnabled = _topmost.IsEnabled = true;
            Render();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        Hide();
    }

    public void ShowSafely()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _service.Changed -= Render;
        Close();
    }

    private sealed class UsageItem : INotifyPropertyChanged
    {
        public string Id { get; }
        public string Name { get; private set; } = "";
        public string Duration { get; private set; } = "";
        public string Share { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public UsageItem(string id) => Id = id;
        public void Update(string name, string duration, string share)
        {
            if (Name == name && Duration == duration && Share == share) return;
            Name = name;
            Duration = duration;
            Share = share;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
