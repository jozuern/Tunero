using System.Windows;
using System.Windows.Controls;
using Optimizer.App.ViewModels;

namespace Optimizer.App.Views;

public partial class SettingsPage : UserControl
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            var theme = (Application.Current as App)?.ThemeSetting ?? "System";
            ThemeBox.SelectedIndex = theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            LanguageBox.SelectedIndex = Services.Loc.Instance.Language == "de" ? 1 : 0;
            _loading = false;
        };
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        (Application.Current as App)?.ApplyTheme(theme);
    }

    // Selecting the language that is already on (when the page loads) must not switch again.
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string lang } || DataContext is not MainViewModel vm) return;
        if (lang != Services.Loc.Instance.Language) vm.SetLanguageCommand.Execute(lang);
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(VtKey.Password)) return;
        vm.SaveVirusTotalKey(VtKey.Password);
        VtKey.Password = "";
        SaveKeyButton.IsEnabled = false;
    }

    private void VtKey_PasswordChanged(object sender, RoutedEventArgs e) => SaveKeyButton.IsEnabled = !string.IsNullOrWhiteSpace(VtKey.Password);

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        new LicensesWindow(url => vm.OpenLinkCommand.Execute(url)) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SaveVirusTotalKey(null);
        VtKey.Password = "";
        SaveKeyButton.IsEnabled = false;
    }
}
