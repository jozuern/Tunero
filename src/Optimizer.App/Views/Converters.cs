using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Optimizer.App.Views;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null && (value is not string s || s.Length > 0);
        return visible ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    /// <summary>Hidden keeps the space (an icon column that lines up the text of every row).</summary>
    public Visibility Off { get; set; } = Visibility.Collapsed;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Off;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Foreground from a theme brush, by resource key or by status ("Ok", "Problem", ...). It is set as a resource reference
/// (like {DynamicResource}), so the color follows a theme switch without building the rows again. Icons, text and
/// controls all share TextElement.ForegroundProperty.
/// </summary>
public static class ThemeBrush
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ThemeBrush), new PropertyMetadata(null, (d, e) => Apply(d, e.NewValue as string)));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.RegisterAttached(
        "Status", typeof(string), typeof(ThemeBrush), new PropertyMetadata(null, (d, e) => Apply(d, e.NewValue is string s ? ViewModels.StatusIcons.BrushKey(s) : null)));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);
    public static string? GetStatus(DependencyObject d) => (string?)d.GetValue(StatusProperty);
    public static void SetStatus(DependencyObject d, string? value) => d.SetValue(StatusProperty, value);

    private static void Apply(DependencyObject d, string? key)
    {
        if (d is not FrameworkElement element) return;
        if (key is null) element.ClearValue(TextElement.ForegroundProperty);
        else element.SetResourceReference(TextElement.ForegroundProperty, key);
    }
}

public sealed class StatusToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ViewModels.StatusIcons.Symbol(value as string ?? "");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Collection count -> visible when above zero (Invert: visible when empty).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int n && n > 0) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Non-empty value -> true (InfoBar.IsOpen bound to an optional message).</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null && (value is not string s || s.Length > 0);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A number at least the parameter -> true (the marks of the benchmark steps reached so far).</summary>
public sealed class AtLeastConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) && n >= min;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A width below the parameter -> true (a page too narrow for the labels next to its switches).</summary>
public sealed class WidthBelowConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double w && w > 0 && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) && w < limit;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when every bound value is true (a row's own "can toggle" and the app-wide "a change may start now").</summary>
public sealed class AllTrueConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) => values.All(v => v is true);
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when both values are the same object (a row and the item shown in the details pane).</summary>
public sealed class SameItemConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The width of a page's content: the page minus its padding and the room of a scroll bar, at most 1000 px (as in
/// Windows Settings). Every page then ends at the same place, also one whose content alone would be narrower.
/// </summary>
public sealed class PageWidthConverter : IValueConverter
{
    public const double Max = 1000, Padding = 48 + 16;

    /// <summary>The widest the content gets; the Storage page allows more (its treemap shows more the wider it is).</summary>
    public double Limit { get; set; } = Max;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double w && w > Padding ? Math.Min(Limit, w - Padding) : double.NaN;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The margin that takes a page's scrolling list out to the page's right edge: past the page padding and the room beside
/// content of at most 1000 px. Its scroll bar then sits where a page that scrolls as a whole has it, at the window edge,
/// and the wheel works over that room too; the list used to end 16 px after its cards, with the scroll bar mid-window.
/// </summary>
public sealed class EdgeMarginConverter : IValueConverter
{
    /// <summary>The page's left padding (PagePadding in App.xaml).</summary>
    public const double PageLeft = 24;

    /// <summary>The content width the margin is measured beside (<see cref="PageWidthConverter.Limit"/> of the same page).</summary>
    public double Limit { get; set; } = PageWidthConverter.Max;

    /// <param name="parameter">The top margin, as a string ("8" under a tab bar).</param>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var top = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0;
        var extra = value is double w && w > PageWidthConverter.Padding
            ? w - PageLeft - Math.Min(Limit, w - PageWidthConverter.Padding)
            : 16;
        return new Thickness(0, top, -extra, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A scrolling list inside a scrolling page: once the list is at its top or bottom, the mouse wheel scrolls the page
/// instead of stopping (the list would otherwise trap the wheel). Set on the inner ItemsControl.
/// </summary>
public static class ScrollChain
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ScrollChain), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject list || Descendant<System.Windows.Controls.ScrollViewer>(list) is not { } inner) return;
        var atEnd = e.Delta > 0 ? inner.VerticalOffset <= 0 : inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
        if (!atEnd) return;
        var outer = VisualTreeHelper.GetParent(list);
        while (outer is not null and not System.Windows.Controls.ScrollViewer) outer = VisualTreeHelper.GetParent(outer);
        if (outer is not System.Windows.Controls.ScrollViewer page) return;
        e.Handled = true;
        page.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = list });
    }

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}

/// <summary>Tweaks page list: a group heading or a tweak row, in one virtualized list.</summary>
public sealed class TweakListTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public DataTemplate? Group { get; set; }
    public DataTemplate? Tweak { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is ViewModels.TweakGroupRow ? Group : Tweak;
}
