using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.App.Views;

namespace Optimizer.App.Tests;

/// <summary>
/// Names screen readers announce where the control that takes focus is not the one the XAML names: the header toggle of
/// an expander, a name built from two texts, a row's icon button. Nothing reads or changes the system.
/// </summary>
public sealed class ScreenReaderNameTests
{
    /// <summary>
    /// The toggle the expander's template makes takes the expander's name and follows it; a check box in the content (a
    /// ToggleButton too) keeps its own.
    /// </summary>
    [Fact]
    public void AnExpandersHeaderToggleTakesTheExpandersName()
    {
        string? toggleName = null, renamed = null, boxName = null;
        bool foundTemplateToggle = false;
        Exception? error = null;
        var sta = new Thread(() =>
        {
            try
            {
                // Content before the header toggle, so a search that took the first ToggleButton would find the check box
                var panel = new FrameworkElementFactory(typeof(StackPanel));
                panel.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                var header = new FrameworkElementFactory(typeof(ToggleButton));
                header.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(Expander.Header)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                panel.AppendChild(header);
                var box = new CheckBox();
                AutomationProperties.SetName(box, "Tick all");
                var expander = new Expander { Template = new ControlTemplate(typeof(Expander)) { VisualTree = panel }, Header = "Group", Content = box, IsExpanded = true };
                AutomationProperties.SetName(expander, "Options for Group");
                expander.Measure(new Size(400, 400));
                expander.Arrange(new Rect(0, 0, 400, 400));

                ExpanderToggle.Bind(expander);
                var toggle = ExpanderToggle.HeaderToggle(expander);
                foundTemplateToggle = toggle is not null && toggle is not CheckBox && ReferenceEquals(toggle.TemplatedParent, expander);
                toggleName = toggle is null ? null : AutomationProperties.GetName(toggle);
                AutomationProperties.SetName(expander, "Options for Other");
                renamed = toggle is null ? null : AutomationProperties.GetName(toggle);
                boxName = AutomationProperties.GetName(box);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        Assert.Null(error);
        Assert.True(foundTemplateToggle);
        Assert.Equal("Options for Group", toggleName);
        Assert.Equal("Options for Other", renamed);
        Assert.Equal("Tick all", boxName);
    }

    /// <summary>{l:Tr Key, Format=Other}: the key's text goes into the other text, in the language in use.</summary>
    [Fact]
    public void ATextCanGoIntoAnotherText()
    {
        var converter = new TrExtension.FormatConverter("Common_OptionsFor");
        var title = Loc.Instance["Media_RemoveAppsTitle"];
        var name = (string)converter.Convert(title, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(Loc.Instance.Format("Common_OptionsFor", title), name);
        Assert.Contains(title, name);
        Assert.NotEqual(title, name);
    }

    /// <summary>Every row's ignore button says which setting it ignores, as Undo and Details in the same row do.</summary>
    [Fact]
    public void TheIgnoreButtonOfARowNamesTheRow()
    {
        var a = new AgainstLine("x.one", "Memory compression off", "Reason", CanUndo: false);
        var b = new AgainstLine("x.two", "Game Mode off", "Reason", CanUndo: true);
        Assert.Equal(Loc.Instance.Format("Rec_AgainstIgnoreName", a.Title), a.IgnoreLabel);
        Assert.Contains(a.Title, a.IgnoreLabel);
        Assert.NotEqual(a.IgnoreLabel, b.IgnoreLabel);
    }
}
