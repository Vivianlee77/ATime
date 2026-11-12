using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ATime.App.Services;

namespace ATime.App.Views;

internal static class UiStyles
{
    public static readonly Brush Paper = Brush("#F6F4ED");
    public static readonly Brush Panel = Brush("#FFFEFA");
    public static readonly Brush Ink = Brush("#263A36");
    public static readonly Brush Muted = Brush("#637269");
    public static readonly Brush Green = Brush("#277765");
    public static readonly Brush Mint = Brush("#E5EEE8");
    public static readonly Brush Line = Brush("#E3E7DE");
    public static readonly Brush Warning = Brush("#96642D");

    private static Brush Brush(string value)
    {
        var result = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
        result.Freeze();
        return result;
    }

    public static TextBlock Text(string text = "", double size = 13, Brush? foreground = null,
        FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground ?? Ink,
        FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    public static Button Button(string text, bool primary = false) => new()
    {
        Content = text,
        Background = primary ? Green : Panel,
        Foreground = primary ? Brushes.White : Ink,
        BorderBrush = primary ? Green : Line,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 7, 12, 7),
        MinHeight = 32,
        Cursor = System.Windows.Input.Cursors.Hand,
        FontSize = 12
    };

    public static string Duration(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds) / 1000;
        if (seconds >= 3600) return $"{seconds / 3600} 小时 {seconds / 60 % 60:00} 分";
        if (seconds >= 60) return $"{seconds / 60} 分 {seconds % 60:00} 秒";
        return $"{seconds} 秒";
    }

    public static string CompactDuration(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds) / 1000;
        if (seconds >= 3600) return $"{seconds / 3600}h {seconds / 60 % 60:00}m";
        if (seconds >= 60) return $"{seconds / 60}m {seconds % 60:00}s";
        return $"{seconds}s";
    }

    public static IReadOnlyList<AppTotal> Aggregate(TrackerViewState state, int days, bool sessionMetric)
    {
        var from = state.Today.AddDays(1 - Math.Max(1, days));
        return state.Rows.Where(row => DateOnly.TryParseExact(row.LocalDate, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day >= from && day <= state.Today)
            .GroupBy(row => row.AppId, StringComparer.Ordinal)
            .Select(group => new AppTotal(group.Key, group.Key == "self" ? "ATime" : group.Last().AppName,
                group.Sum(row => Math.Max(0, sessionMetric ? row.SessionMs : row.EffectiveMs))))
            .Where(row => row.Milliseconds > 0)
            .OrderByDescending(row => row.Milliseconds)
            .ThenBy(row => row.Name, StringComparer.CurrentCulture)
            .ToArray();
    }

    public static void ShowError(Window owner, Exception exception)
    {
        MessageBox.Show(owner, "操作未完成。\n\n" + exception.Message, "ATime",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal sealed record AppTotal(string Id, string Name, long Milliseconds);
}
