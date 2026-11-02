using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Optimizer.App.Views;

/// <summary>
/// Gives the header toggle of a CardExpander or Expander the expander's own AutomationProperties.Name. Tab and screen
/// readers land on that toggle (a ToggleButton in WPF-UI's template), not on the expander, so a name set on the expander
/// was never heard: Narrator said only "button". Every expander in the app sets a name (XamlRulesTests). Not as an
/// implicit style for the expander types: even based on WPF-UI's own, the CardExpander lost its card template.
/// </summary>
public static class ExpanderToggle
{
    /// <summary>
    /// Once at start, before any window: an expander that gets a name binds its toggle to it when it is loaded. (A class
    /// handler for Loaded would not do: WPF raises Loaded only on elements with an instance handler.)
    /// </summary>
    public static void Register() =>
        AutomationProperties.NameProperty.OverrideMetadata(typeof(Expander), new UIPropertyMetadata(string.Empty, OnNameChanged));

    private static void OnNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement expander) return;
        expander.Loaded -= OnLoaded;
        expander.Loaded += OnLoaded;
        if (expander.IsLoaded) Bind(expander);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement expander) Bind(expander);
    }

    /// <summary>Binds the header toggle's name to the expander's, once per toggle (a new template brings a new toggle).</summary>
    internal static void Bind(FrameworkElement expander)
    {
        expander.ApplyTemplate();
        if (HeaderToggle(expander) is not { } toggle || BindingOperations.IsDataBound(toggle, AutomationProperties.NameProperty)) return;
        BindingOperations.SetBinding(toggle, AutomationProperties.NameProperty,
            new Binding { Path = new PropertyPath(AutomationProperties.NameProperty), Source = expander, Mode = BindingMode.OneWay });
    }

    /// <summary>
    /// The header's toggle button: the first one the expander's own template made. A check box in the header (the install
    /// media groups) is a ToggleButton too, but it comes from the expander's content, not from its template.
    /// </summary>
    internal static ToggleButton? HeaderToggle(FrameworkElement expander) => FirstToggle(expander, expander);

    private static ToggleButton? FirstToggle(DependencyObject parent, FrameworkElement expander)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ToggleButton toggle && ReferenceEquals(toggle.TemplatedParent, expander)) return toggle;
            if (FirstToggle(child, expander) is { } deeper) return deeper;
        }
        return null;
    }
}
