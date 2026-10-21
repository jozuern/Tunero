using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Optimizer.App.Views;

public partial class StartupPage : UserControl
{
    public StartupPage() => InitializeComponent();

    /// <summary>
    /// More options opens its menu under the button on a click, Enter or Space; the menu takes the keyboard focus, and
    /// Escape closes it again. Shift+F10 and the menu key open it as any context menu.
    /// </summary>
    private void OnMoreOptions(object sender, RoutedEventArgs e) => OpenMenu(sender as FrameworkElement);

    internal static void OpenMenu(FrameworkElement? button)
    {
        if (button?.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
