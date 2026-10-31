using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Optimizer.App.Views;

/// <summary>
/// Puts a CardExpander's chevron at the top of its header, the given distance down, instead of in the middle: for a header
/// with a title row and more below it (the Health page's sensor cards with their values), so the chevron stands beside the
/// title. WPF-UI's template centers it ("ChevronGrid") and offers no setting for that.
/// </summary>
public static class ExpanderChevron
{
    public static readonly DependencyProperty TopProperty = DependencyProperty.RegisterAttached(
        "Top", typeof(double), typeof(ExpanderChevron), new PropertyMetadata(double.NaN, OnTopChanged));

    public static double GetTop(DependencyObject d) => (double)d.GetValue(TopProperty);
    public static void SetTop(DependencyObject d, double value) => d.SetValue(TopProperty, value);

    private static void OnTopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement expander) return;
        expander.Loaded -= Apply;
        expander.Loaded += Apply;
        if (expander.IsLoaded) Apply(expander, new RoutedEventArgs());
    }

    private static void Apply(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement expander || double.IsNaN(GetTop(expander))) return;
        expander.ApplyTemplate();
        // The header's toggle button is the first one in the template; the card's content comes after it.
        if (FirstToggle(expander) is not { } toggle) return;
        toggle.ApplyTemplate();
        if (toggle.Template?.FindName("ChevronGrid", toggle) is FrameworkElement chevron)
        {
            chevron.VerticalAlignment = VerticalAlignment.Top;
            chevron.Margin = new Thickness(0, GetTop(expander), 0, 0);
        }
    }

    private static ToggleButton? FirstToggle(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ToggleButton toggle) return toggle;
            if (FirstToggle(child) is { } deeper) return deeper;
        }
        return null;
    }
}
