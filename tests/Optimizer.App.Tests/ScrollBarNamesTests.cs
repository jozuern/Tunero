using System.Globalization;
using System.Resources;
using System.Windows.Controls.Primitives;
using Optimizer.App.Services;

namespace Optimizer.App.Tests;

/// <summary>The arrow buttons of WPF-UI's scroll bars get real names in both languages instead of their icon names.</summary>
public class ScrollBarNamesTests
{
    private static readonly ResourceManager Strings = new("Optimizer.App.Resources.Strings", typeof(Loc).Assembly);

    [Fact]
    public void EveryArrowCommandHasANameInBothLanguages()
    {
        var keys = new[] { ScrollBar.LineUpCommand, ScrollBar.LineDownCommand, ScrollBar.LineLeftCommand, ScrollBar.LineRightCommand }
            .Select(ScrollBarNames.KeyFor).ToList();
        Assert.Equal(new string?[] { "Scroll_Up", "Scroll_Down", "Scroll_Left", "Scroll_Right" }, keys);
        foreach (var key in keys)
        {
            var en = Strings.GetString(key!, CultureInfo.GetCultureInfo("en"));
            var de = Strings.GetString(key!, CultureInfo.GetCultureInfo("de"));
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(de), key);
            Assert.NotEqual(en, de);
        }
        // Page buttons (no content, no icon name) and every other button keep what they have.
        Assert.Null(ScrollBarNames.KeyFor(ScrollBar.PageUpCommand));
        Assert.Null(ScrollBarNames.KeyFor(null));
    }
}
