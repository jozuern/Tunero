using System.Windows;
using System.Windows.Controls;

namespace Optimizer.App.Views;

/// <summary>
/// The top of a page with a command (Refresh, Scan again, a filter switch): the title with the command beside it, and the
/// intro under both at the page's full width. With the command docked beside title and intro together, the intro wrapped
/// early and left a single word on its last line. The content is the command; the template is in App.xaml.
/// </summary>
public class PageHeader : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader));
    public static readonly DependencyProperty IntroProperty = DependencyProperty.Register(nameof(Intro), typeof(string), typeof(PageHeader));

    static PageHeader()
    {
        // Layout, not a control: Tab goes straight to the command
        FocusableProperty.OverrideMetadata(typeof(PageHeader), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(PageHeader), new FrameworkPropertyMetadata(false));
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Intro
    {
        get => (string?)GetValue(IntroProperty);
        set => SetValue(IntroProperty, value);
    }
}
