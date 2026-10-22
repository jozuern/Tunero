using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Optimizer.App.ViewModels;

namespace Optimizer.App.Views;

public partial class InstallMediaPage : UserControl
{
    private InstallMediaViewModel? _media;

    public InstallMediaPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Watch();
        Unloaded += (_, _) => Unwatch();
    }

    private void Watch()
    {
        Unwatch();
        if (DataContext is not MainViewModel main) return;
        _media = main.InstallMedia;
        _media.StagesShown += ShowStages;
    }

    private void Unwatch()
    {
        if (_media is not null) _media.StagesShown -= ShowStages;
        _media = null;
    }

    /// <summary>Build was pressed: the list of stages scrolls into view (the card appears with the first build).</summary>
    private void ShowStages(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => StagesCard.BringIntoView());
}
