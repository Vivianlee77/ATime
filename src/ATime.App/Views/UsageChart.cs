using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ATime.App.Services;

namespace ATime.App.Views;

public sealed class UsageChart : UserControl
{
    private static readonly Brush PastBar = FrozenBrush("#8FAFA1");
    private readonly TextBlock _title = UiStyles.Text("一天的节奏", 17, weight: FontWeights.SemiBold);
    private readonly TextBlock _legend = UiStyles.Text("有效使用", 12, UiStyles.Muted);
    private readonly TextBlock _caption = UiStyles.Text("", 11, UiStyles.Muted);
    private readonly TextBlock _empty = UiStyles.Text("", 12, UiStyles.Muted);
    private readonly Canvas _axis = new();
    private readonly Canvas _plot = new() { Background = Brushes.Transparent };
    private readonly Canvas _labels = new();
    private readonly List<BarSlot> _bars = [];
    private readonly List<(Line Line, TextBlock Label)> _ticks = [];
    private IReadOnlyList<ChartPoint> _points = [];
    private double _maximumMinutes = 60;

    public UsageChart()
    {
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(192) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2),
            Background = UiStyles.Green, Margin = new Thickness(0, 0, 8, 0) });
        legend.Children.Add(_legend);
        DockPanel.SetDock(legend, Dock.Right);
        heading.Children.Add(legend);
        heading.Children.Add(_title);
        body.Children.Add(heading);

        var graph = new Grid();
        graph.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        graph.ColumnDefinitions.Add(new ColumnDefinition());
        graph.RowDefinitions.Add(new RowDefinition { Height = new GridLength(164) });
        graph.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        graph.Children.Add(_axis);
        Grid.SetColumn(_plot, 1);
        graph.Children.Add(_plot);
        Grid.SetColumn(_labels, 1);
        Grid.SetRow(_labels, 1);
        graph.Children.Add(_labels);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.IsHitTestVisible = false;
        Grid.SetColumn(_empty, 1);
        graph.Children.Add(_empty);
        Grid.SetRow(graph, 1);
        body.Children.Add(graph);
        _caption.Margin = new Thickness(40, 5, 0, 0);
        _caption.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_caption, 2);
        body.Children.Add(_caption);
        Content = new Border { Background = UiStyles.Panel, BorderBrush = UiStyles.Line,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 16, 18, 14), Child = body };
        for (var index = 0; index < 3; index++)
        {
            var line = new Line { Stroke = UiStyles.Line, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection([3, 3]), IsHitTestVisible = false };
            var label = UiStyles.Text("", 11, UiStyles.Muted);
            _plot.Children.Add(line);
            _axis.Children.Add(label);
            _ticks.Add((line, label));
        }
        _plot.SizeChanged += (_, _) => ArrangeChart();
        AutomationProperties.SetName(this, "使用时间趋势图");
    }

    public void Render(TrackerViewState state, int days, bool sessionMetric)
    {
        _legend.Text = sessionMetric ? "屏幕会话" : "有效使用";
        var points = new List<ChartPoint>();
        if (days == 1)
        {
            _title.Text = "一天的节奏";
            var hours = state.HourlyRows.OrderBy(row => row.Hour).ToArray();
            var used = hours.Where(row => (sessionMetric ? row.SessionMs : row.EffectiveMs) > 0).Select(row => row.Hour).ToArray();
            var end = Math.Max(state.CurrentHour, used.LastOrDefault(state.CurrentHour));
            var start = Math.Min(used.FirstOrDefault(Math.Max(0, state.CurrentHour - 7)), Math.Max(0, end - 7));
            end = Math.Min(23, Math.Max(end, start + 7));
            for (var hour = start; hour <= end; hour++)
            {
                var row = hours.FirstOrDefault(row => row.Hour == hour);
                var duration = row is null ? 0 : sessionMetric ? row.SessionMs : row.EffectiveMs;
                var interval = $"{state.Today:MM-dd}  {hour:00}:00–{hour + 1:00}:00";
                points.Add(new(hour.ToString("00", CultureInfo.InvariantCulture), duration,
                    hour == state.CurrentHour, interval));
            }
            _caption.Text = "单位：分钟 · 每个时段只归属一个前台应用 · 深色为当前小时";
            _empty.Text = sessionMetric ? "今天还没有屏幕会话记录" : "今天还没有有效使用记录，开始使用电脑后会在这里显示";
        }
        else
        {
            _title.Text = "每日使用趋势";
            var daily = state.Rows.GroupBy(row => row.LocalDate).ToDictionary(group => group.Key,
                group => group.Sum(row => sessionMetric ? row.SessionMs : row.EffectiveMs));
            for (var index = 1 - days; index <= 0; index++)
            {
                var date = state.Today.AddDays(index);
                daily.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var duration);
                points.Add(new(date.ToString("MM.dd", CultureInfo.InvariantCulture), duration, index == 0,
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            _caption.Text = $"单位：分钟 · 近 {days} 日，含今天 · 深色为今天";
            _empty.Text = "这个日期范围还没有使用记录";
        }
        SetPoints(points);
    }

    private void SetPoints(IReadOnlyList<ChartPoint> points)
    {
        _points = points;
        var largest = points.Count == 0 ? 0 : points.Max(point => point.Milliseconds) / 60000d;
        _maximumMinutes = new double[] { 60, 120, 240, 480, 720, 1440, 2880 }
            .FirstOrDefault(value => value >= largest, Math.Ceiling(largest / 1440) * 1440);
        if (_bars.Count != points.Count)
        {
            foreach (var slot in _bars) { _plot.Children.Remove(slot.HitArea); _labels.Children.Remove(slot.Label); }
            _bars.Clear();
            foreach (var _ in points)
            {
                var bar = new Border { CornerRadius = new CornerRadius(6, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
                var hit = new Grid { Background = Brushes.Transparent };
                hit.Children.Add(bar);
                ToolTipService.SetInitialShowDelay(hit, 100);
                ToolTipService.SetShowDuration(hit, 30000);
                var label = UiStyles.Text("", 11, UiStyles.Muted);
                label.TextAlignment = TextAlignment.Center;
                _plot.Children.Add(hit);
                _labels.Children.Add(label);
                _bars.Add(new(hit, bar, label));
            }
        }
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var slot = _bars[index];
            slot.Label.Text = point.Label;
            slot.Bar.Background = point.Current ? UiStyles.Green : PastBar;
            var tooltip = $"{point.Interval}\n{_legend.Text}：{UiStyles.Duration(point.Milliseconds)}";
            slot.HitArea.ToolTip = tooltip;
            AutomationProperties.SetName(slot.HitArea, tooltip);
        }
        _empty.Visibility = points.All(point => point.Milliseconds == 0) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, _title.Text + "，" + _legend.Text);
        AutomationProperties.SetHelpText(this, string.Join("；", points.Select(point => point.Interval + " " + UiStyles.Duration(point.Milliseconds))));
        ArrangeChart();
    }

    private void ArrangeChart()
    {
        var width = _plot.ActualWidth;
        var height = _plot.ActualHeight;
        if (width <= 0 || height <= 0 || _points.Count == 0) return;
        for (var index = 0; index < _ticks.Count; index++)
        {
            var y = 8 + (height - 9) * index / 2;
            var tick = _ticks[index];
            tick.Line.X1 = 0; tick.Line.X2 = width; tick.Line.Y1 = tick.Line.Y2 = y;
            tick.Label.Text = (_maximumMinutes * (2 - index) / 2).ToString("0.#", CultureInfo.InvariantCulture);
            Canvas.SetTop(tick.Label, y - 8);
        }
        var cell = width / _points.Count;
        var labelInterval = Math.Max(1, (int)Math.Ceiling(_points.Count / Math.Max(1, width / 44)));
        var labelWidth = Math.Max(44, cell);
        var currentIndex = _points.ToList().FindIndex(point => point.Current);
        for (var index = 0; index < _bars.Count; index++)
        {
            var slot = _bars[index];
            var point = _points[index];
            slot.HitArea.Width = cell;
            slot.HitArea.Height = height - 1;
            Canvas.SetLeft(slot.HitArea, cell * index);
            slot.Bar.Width = Math.Min(58, Math.Max(4, cell * 0.60));
            slot.Bar.Height = point.Milliseconds <= 0 ? 0 : Math.Max(1, point.Milliseconds / 60000d / _maximumMinutes * (height - 9));
            slot.Label.Width = labelWidth;
            var showLabel = point.Current || index % labelInterval == 0 &&
                (currentIndex < 0 || Math.Abs(index - currentIndex) >= labelInterval);
            slot.Label.Visibility = showLabel ? Visibility.Visible : Visibility.Hidden;
            Canvas.SetLeft(slot.Label, Math.Clamp(cell * (index + 0.5) - labelWidth / 2, 0, Math.Max(0, width - labelWidth)));
            Canvas.SetTop(slot.Label, 8);
        }
    }

    private static Brush FrozenBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private sealed record ChartPoint(string Label, long Milliseconds, bool Current, string Interval);
    private sealed record BarSlot(Grid HitArea, Border Bar, TextBlock Label);
}
