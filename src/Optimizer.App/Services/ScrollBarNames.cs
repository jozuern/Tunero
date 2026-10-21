using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace Optimizer.App.Services;

/// <summary>
/// WPF-UI's scroll bar templates put a SymbolRegular value (CaretUp24, CaretDown24, CaretLeft24, CaretRight24) into
/// their arrow buttons as content, so UI Automation names them after the icon and a screen reader reads "CaretUp24".
/// Every arrow button of a scroll bar gets a real, localized name instead. Not as a style: the templates set the
/// buttons' style themselves, and replacing the templates would lose WPF-UI's own.
/// </summary>
internal static class ScrollBarNames
{
    private static readonly (RoutedCommand Command, string Key)[] Names =
    [
        (ScrollBar.LineUpCommand, "Scroll_Up"),
        (ScrollBar.LineDownCommand, "Scroll_Down"),
        (ScrollBar.LineLeftCommand, "Scroll_Left"),
        (ScrollBar.LineRightCommand, "Scroll_Right"),
    ];

    /// <summary>
    /// Once at start, before any window: a RepeatButton gets its name when the template sets its command. (A class
    /// handler for Loaded would not do: WPF raises Loaded only on elements with an instance handler.)
    /// </summary>
    public static void Register() =>
        ButtonBase.CommandProperty.OverrideMetadata(typeof(RepeatButton), new FrameworkPropertyMetadata(OnCommandChanged));

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RepeatButton button || KeyFor(e.NewValue as ICommand) is not { } key) return;
        // Bound, so a language switch renames it too.
        button.SetBinding(AutomationProperties.NameProperty, new Binding($"[{key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay });
    }

    /// <summary>The string key for a scroll bar's arrow command; null for every other button (page buttons have no content).</summary>
    public static string? KeyFor(ICommand? command)
    {
        foreach (var (c, key) in Names)
            if (ReferenceEquals(c, command)) return key;
        return null;
    }
}
