using System.Windows;
using System.Windows.Controls;
using Optimizer.App.Services;
using Wpf.Ui.Controls;

namespace Optimizer.App.Views;

/// <summary>Tunero's own license and the license texts of every shipped component.</summary>
public partial class LicensesWindow : FluentWindow
{
    private readonly Action<string> _openLink;

    /// <param name="openLink">Opens a URL de-elevated (the app runs as administrator).</param>
    public LicensesWindow(Action<string> openLink)
    {
        _openLink = openLink;
        InitializeComponent();
        Loaded += (_, _) => IconButtonNames.Apply(this);
        Title = Loc.Instance["Licenses_Title"];
        ComponentList.ItemsSource = Licenses.Components;
        ComponentList.SelectedIndex = 0;
    }

    private void Component_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ComponentList.SelectedItem is not LicenseComponent c) return;
        ComponentName.Text = c.Name;
        ComponentSource.Text = c.Source;
        LicenseText.Text = Licenses.Text(c);
        LicenseText.ScrollToHome();
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (ComponentList.SelectedItem is LicenseComponent c) _openLink(c.Source);
    }
}
