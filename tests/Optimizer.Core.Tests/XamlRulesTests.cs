using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Optimizer.Core.Tests;

/// <summary>Rules for the app's XAML that a screenshot cannot show.</summary>
public partial class XamlRulesTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// A hand cursor set on a TabItem reaches the whole tab's content (its mouse events route through the TabItem), so every
    /// text and card on a page with tabs showed as clickable. The hand belongs on the header inside the tab's template.
    /// </summary>
    [Fact]
    public void TabItemsSetNoCursorForTheirContent()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.App, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var doc = XDocument.Load(file);
            var relative = Path.GetRelativePath(RepoPaths.App, file);
            foreach (var style in doc.Descendants(Presentation + "Style").Where(s => TabItemType().IsMatch((string?)s.Attribute("TargetType") ?? "")))
                if (style.Elements(Presentation + "Setter").Any(s => (string?)s.Attribute("Property") == "Cursor"))
                    offenders.Add($"{relative}: a TabItem style sets Cursor");
            foreach (var item in doc.Descendants(Presentation + "TabItem").Where(i => i.Attribute("Cursor") is not null))
                offenders.Add($"{relative}: a TabItem sets Cursor");
        }
        Assert.Empty(offenders);
    }

    /// <summary>
    /// Tab headers stand in one row that never wraps (SelectorTabs and SelectorTabHeaders in App.xaml): WPF's TabPanel, also
    /// inside WPF-UI's TabControl template, broke a long row into two, stretched the tabs and moved the selected row down.
    /// </summary>
    [Fact]
    public void TabRowsNeverWrap()
    {
        var offenders = new List<string>();
        foreach (var (relative, doc) in AppXaml())
        {
            foreach (var panel in doc.Descendants(Presentation + "TabPanel"))
                offenders.Add($"{relative}:{((IXmlLineInfo)panel).LineNumber}: a TabPanel (wraps; use the SelectorTabs style)");
            foreach (var tabs in doc.Descendants(Presentation + "TabControl"))
                if ((string?)tabs.Attribute("Style") != "{StaticResource SelectorTabs}" && (string?)tabs.Attribute("Template") != "{StaticResource SelectorTabHeaders}")
                    offenders.Add($"{relative}:{((IXmlLineInfo)tabs).LineNumber}: a TabControl without the SelectorTabs style");
        }
        Assert.Empty(offenders);
    }

    /// <summary>
    /// The navigation rail uses NavItem and NavHeader (Views/NavItem.cs): WPF-UI's own item gives screen readers no way to
    /// open a page, and its heading is not read at all. Both need the switch that removes the ItemsControl's item wrapper.
    /// </summary>
    [Fact]
    public void TheNavigationRailIsReachableForScreenReaders()
    {
        var window = File.ReadAllText(Path.Combine(RepoPaths.App, "Views", "MainWindow.xaml"));
        Assert.DoesNotContain("<ui:NavigationViewItem ", window);
        Assert.DoesNotContain("<ui:NavigationViewItemHeader ", window);
        Assert.Contains("<v:NavItem ", window);
        Assert.Contains("<v:NavHeader ", window);
        var project = File.ReadAllText(Path.Combine(RepoPaths.App, "Optimizer.App.csproj"));
        Assert.Contains("\"Switch.System.Windows.Controls.ItemsControlDoesNotSupportAutomation\" Value=\"true\"", project);
    }

    /// <summary>
    /// Cards take their padding from CardPadding (App.xaml), so a page does not show three paddings in a row. The exceptions
    /// below differ on purpose; each says why in a comment next to the card.
    /// </summary>
    [Fact]
    public void CardsUseTheSharedPadding()
    {
        // (file, padding) -> how many cards may have it, and why
        var allowed = new Dictionary<(string File, string Padding), (int Count, string Reason)>
        {
            [("Views/HardwarePage.xaml", "16")] = (1, "the tiles in a grid: the same space on every side"),
            [("Views/InstallMediaPage.xaml", "16")] = (1, "the finished build: room around a 48 px check mark and a subtitle"),
            [("Views/StoragePage.xaml", "0,8")] = (2, "list cards whose rows' highlights come nearer the edges; the rows set their own insets"),
            [("Views/NetworkPage.xaml", "16,6")] = (1, "the compact one-line game rows"),
        };
        var found = new Dictionary<(string File, string Padding), List<string>>();
        foreach (var (relative, doc) in AppXaml())
            foreach (var card in doc.Descendants().Where(e => e.Name.LocalName is "Card" or "CardControl" or "ClickCard"))
                if ((string?)card.Attribute("Padding") is { } padding && !padding.StartsWith('{'))
                {
                    var key = (relative, padding);
                    if (!found.TryGetValue(key, out var lines))
                        found[key] = lines = [];
                    lines.Add($"{relative}:{((IXmlLineInfo)card).LineNumber}: {card.Name.LocalName} Padding=\"{padding}\"");
                }
        var offenders = found
            .Where(f => !allowed.TryGetValue(f.Key, out var allow) || f.Value.Count > allow.Count)
            .SelectMany(f => f.Value)
            .Select(l => l + " (use {StaticResource CardPadding}, or add the card to the allow list with its reason)")
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>
    /// The line a list shows when it is empty or nothing matches uses the EmptyState style (App.xaml), so empty states
    /// look the same on every page. A text counts as one when its text key or the property that shows it ends in Empty,
    /// NoMatch or _None, or its text is bound to an EmptyText property: name new empty states that way. Not a list's empty
    /// state, so not covered: the Storage page before its first analysis (a centered title under a large icon) and the
    /// result line "No duplicates found".
    /// </summary>
    [Fact]
    public void EmptyStatesUseTheSharedStyle()
    {
        var offenders = new List<string>();
        var count = 0;
        foreach (var (relative, doc) in AppXaml())
            foreach (var text in doc.Descendants().Where(e => e.Name.LocalName == "TextBlock"))
            {
                var textValue = (string?)text.Attribute("Text") ?? "";
                var key = TrKey().Match(textValue) is { Success: true } m ? m.Groups[1].Value : null;
                var isEmptyState = (key is not null && EmptyName().IsMatch(key))
                                   || BoundToEmptyText().IsMatch(textValue)
                                   || ShownWhenEmpty().IsMatch((string?)text.Attribute("Visibility") ?? "");
                if (!isEmptyState)
                    continue;
                count++;
                if ((string?)text.Attribute("Style") != "{StaticResource EmptyState}")
                    offenders.Add($"{relative}:{((IXmlLineInfo)text).LineNumber}: {textValue} (use Style=\"{{StaticResource EmptyState}}\")");
            }
        Assert.Empty(offenders);
        // The rule finds what it is meant to find: a rename that hides every empty state from it fails here
        Assert.True(count >= 15, $"only {count} empty states found");
    }

    /// <summary>
    /// A switch says what it is or what state it is in (App.xaml, before SettingSwitch): a setting in a card shows On or Off
    /// (SettingSwitch), a filter has its label as Content, a row's switch names the row's state with OnContent and OffContent
    /// bindings (or its own style, as TweakSwitch). On and Off written out next to a switch belong in SettingSwitch.
    /// </summary>
    [Fact]
    public void SwitchesSayWhatTheyShow()
    {
        // (file, IsChecked binding) -> why the switch shows none of these
        var allowed = new Dictionary<(string File, string IsChecked), string>
        {
            [("Views/InstallMediaPage.xaml", "{Binding InstallMedia.RemoveApps}")] = "a summary left of the switch says what is on, or Off",
            [("Views/InstallMediaPage.xaml", "{Binding InstallMedia.RemoveParts}")] = "a summary left of the switch says what is on, or Off",
            [("Views/InstallMediaPage.xaml", "{Binding InstallMedia.SkipSetup}")] = "a summary left of the switch says what is on, or Off",
            [("Views/InstallMediaPage.xaml", "{Binding InstallMedia.ApplyProfile}")] = "a summary left of the switch says what is on, or Off",
            [("Views/InstallMediaPage.xaml", "{Binding InstallMedia.InstallApps}")] = "a summary left of the switch says what is on, or Off",
            [("Views/InstallMediaPage.xaml", "{Binding InstallMedia.RemoveUnreviewed}")] = "a summary left of the switch says what is on, or Off",
        };
        var offenders = new List<string>();
        foreach (var (relative, doc) in AppXaml())
            foreach (var toggle in doc.Descendants().Where(e => e.Name.LocalName == "ToggleSwitch"))
            {
                var on = (string?)toggle.Attribute("OnContent");
                var ok = (string?)toggle.Attribute("Style") == "{StaticResource SettingSwitch}"
                         || (toggle.Attribute("Content") is not null && on is null)
                         || (on?.StartsWith("{Binding", StringComparison.Ordinal) ?? false)
                         || (on is null && toggle.Elements().Any(e => e.Name.LocalName == "ToggleSwitch.Style"))
                         || allowed.ContainsKey((relative, (string?)toggle.Attribute("IsChecked") ?? ""));
                if (!ok)
                    offenders.Add($"{relative}:{((IXmlLineInfo)toggle).LineNumber}: a ToggleSwitch without SettingSwitch, a label or its state words");
            }
        Assert.Empty(offenders);
    }

    /// <summary>The app's XAML files with forward-slash paths relative to the app project, line numbers kept.</summary>
    private static IEnumerable<(string Relative, XDocument Doc)> AppXaml() =>
        Directory.EnumerateFiles(RepoPaths.App, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(RepoPaths.App, f).Replace(Path.DirectorySeparatorChar, '/'), XDocument.Load(f, LoadOptions.SetLineInfo)));

    [GeneratedRegex(@"^(\{x:Type\s+)?TabItem\}?$")]
    private static partial Regex TabItemType();

    [GeneratedRegex(@"^\{\w+:Tr\s+(\w+)\}$")]
    private static partial Regex TrKey();

    [GeneratedRegex(@"(Empty|NoMatch|_None)$|^Empty_")]
    private static partial Regex EmptyName();

    [GeneratedRegex(@"^\{Binding\s+([\w.]+\.)?\w*EmptyText\}$")]
    private static partial Regex BoundToEmptyText();

    [GeneratedRegex(@"^\{Binding\s+([\w.]+\.)?\w*(Empty|NoMatch),\s*Converter=\{StaticResource\s+BoolToVisibility\}\}$|Converter=\{StaticResource\s+EmptyToVisibility\}")]
    private static partial Regex ShownWhenEmpty();
}
