using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;

namespace Optimizer.App.Services;

/// <summary>
/// The navigation pane button and the title bar buttons come from WPF-UI's templates with an icon only, so a screen
/// reader said "button" and Voice Access had no name to click: they get names in the app language, bound, so a language
/// switch renames them. Every window with a WPF-UI title bar calls <see cref="Apply"/> once it is loaded (the main
/// window, the confirmation, the question dialog and the Licenses window).
/// </summary>
internal static class IconButtonNames
{
    /// <summary>The template part's name (or automation id) and the string key of its name.</summary>
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        ["PART_ToggleButton"] = "Nav_Toggle",
        ["TitleBarMinimizeButton"] = "Window_Minimize",
        ["TitleBarMaximizeButton"] = "Window_Maximize",
        ["TitleBarCloseButton"] = "Common_Close",
    };

    /// <summary>The string key for a part of these templates; null for every other element.</summary>
    public static string? KeyFor(string name, string automationId) =>
        Keys.TryGetValue(name, out var key) || Keys.TryGetValue(automationId, out key) ? key : null;

    /// <summary>Names the icon buttons in the visual tree under <paramref name="root"/> (after its templates are applied).</summary>
    public static void Apply(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>([root]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element is FrameworkElement fe && KeyFor(fe.Name, AutomationProperties.GetAutomationId(fe)) is { } key)
                fe.SetBinding(AutomationProperties.NameProperty, new Binding($"[{key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay });
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) pending.Push(VisualTreeHelper.GetChild(element, i));
        }
    }
}
