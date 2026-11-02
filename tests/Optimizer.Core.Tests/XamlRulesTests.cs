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
    /// WPF-UI's title bar buttons carry an icon only: every window with one names them once loaded (IconButtonNames), not
    /// only the main window, so a screen reader and Voice Access do not meet a nameless Close button in a dialog.
    /// </summary>
    [Fact]
    public void EveryTitleBarGetsButtonNames()
    {
        var windows = AppXaml().Where(x => x.Doc.Descendants().Any(e => e.Name.LocalName == "TitleBar")).Select(x => x.Relative).ToList();
        Assert.True(windows.Count >= 4, $"only {windows.Count} windows with a title bar found");
        var missing = windows.Where(w => !File.ReadAllText(Path.Combine(RepoPaths.App, w + ".cs")).Contains("IconButtonNames.Apply(this)", StringComparison.Ordinal)).ToList();
        Assert.Empty(missing);
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
        // (file, IsChecked binding) -> why the switch shows none of these. Empty: the install media cards had On or Off in a
        // summary left of the switch, the only switches in the app with the state on the other side.
        var allowed = new Dictionary<(string File, string IsChecked), string>();
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

    /// <summary>
    /// Tab and screen readers land on an expander's header toggle, not on the expander: the toggle takes the expander's
    /// AutomationProperties.Name (Views/ExpanderToggle.cs). Without one, Narrator said only "button".
    /// </summary>
    [Fact]
    public void ExpandersHaveAName()
    {
        var offenders = new List<string>();
        var count = 0;
        foreach (var (relative, doc) in AppXaml())
            foreach (var expander in doc.Descendants().Where(e => e.Name.LocalName is "CardExpander" or "Expander"))
            {
                count++;
                if (expander.Attribute("AutomationProperties.Name") is null)
                    offenders.Add($"{relative}:{((IXmlLineInfo)expander).LineNumber}: an expander without AutomationProperties.Name");
            }
        Assert.Empty(offenders);
        Assert.True(count >= 9, $"only {count} expanders found");
    }

    /// <summary>
    /// KeyboardNavigation.TabIndex orders Tab stops and Tab groups only. On a scroll viewer that is neither, WPF ignored it,
    /// and the tab headers of every SelectorTabs page came after the whole tab content.
    /// </summary>
    [Fact]
    public void TabIndexSitsOnAStopOrAGroup()
    {
        var offenders = new List<string>();
        foreach (var (relative, doc) in AppXaml())
            foreach (var scroll in doc.Descendants(Presentation + "ScrollViewer"))
                if (scroll.Attribute("KeyboardNavigation.TabIndex") is not null && scroll.Attribute("KeyboardNavigation.TabNavigation") is null)
                    offenders.Add($"{relative}:{((IXmlLineInfo)scroll).LineNumber}: KeyboardNavigation.TabIndex on a ScrollViewer (no Tab stop or group)");
        Assert.Empty(offenders);

        // The tab row is the group: index 1, before the content's 2
        var app = XDocument.Load(Path.Combine(RepoPaths.App, "App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tabs = app.Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(x + "Key") == "SelectorTabs");
        var row = tabs.Descendants(Presentation + "StackPanel").Single(p => (string?)p.Attribute("IsItemsHost") == "True");
        Assert.Equal("1", (string?)row.Attribute("KeyboardNavigation.TabIndex"));
    }

    /// <summary>
    /// TextFillColorTertiaryBrush measures 3.25:1 on a light card, below the 4.5:1 WCAG AA asks for small text. It is for
    /// placeholders, disabled or ended text and icons (3:1 is enough for those); a line that says something uses the
    /// secondary brush. The exceptions below are those cases.
    /// </summary>
    [Fact]
    public void TertiaryTextIsOnlyForPlaceholdersAndDisabledText()
    {
        var allowed = new Dictionary<string, (int Count, string Reason)>
        {
            ["Views/ConfirmWindow.xaml"] = (1, "a value that already is what the change writes, so it reads as no change"),
            ["Views/HealthPage.xaml"] = (2, "the game picker's placeholder, and the rows of programs that ended"),
            ["Views/MainWindow.xaml"] = (1, "the version under the navigation"),
            ["Views/InstallMediaPage.xaml"] = (1, "the icon of a stage that has not started"),
            ["Views/ServicesPage.xaml"] = (1, "the lock icon of a service that cannot be changed"),
        };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.App, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var relative = Path.GetRelativePath(RepoPaths.App, file).Replace(Path.DirectorySeparatorChar, '/');
            var count = Regex.Matches(XamlComment().Replace(File.ReadAllText(file), ""), "TextFillColorTertiaryBrush").Count;
            var allow = allowed.TryGetValue(relative, out var a) ? a.Count : 0;
            if (count > allow)
                offenders.Add($"{relative}: {count} uses of TextFillColorTertiaryBrush, {allow} allowed (use TextFillColorSecondaryBrush for text that says something)");
        }
        Assert.Empty(offenders);
    }

    /// <summary>
    /// A check box in front of a row's title has no text of its own and uses RowCheckBox (App.xaml): with WPF-UI's padding
    /// and a margin of its own, the title stood 19 px from the box instead of 8 as on Cleanup.
    /// </summary>
    [Fact]
    public void CheckBoxesWithoutTextUseTheRowStyle()
    {
        var offenders = new List<string>();
        foreach (var (relative, doc) in AppXaml())
            foreach (var box in doc.Descendants(Presentation + "CheckBox"))
                if (box.Attribute("Content") is null && !box.Elements().Any(e => !e.Name.LocalName.StartsWith("CheckBox.", StringComparison.Ordinal))
                    && (string?)box.Attribute("Style") != "{StaticResource RowCheckBox}")
                    offenders.Add($"{relative}:{((IXmlLineInfo)box).LineNumber}: a CheckBox without text and without Style=\"{{StaticResource RowCheckBox}}\"");
        Assert.Empty(offenders);
    }

    /// <summary>
    /// UI Automation leaves out a TextBlock that sits straight in a control's template (TextBlockAutomationPeer counts only
    /// one without a templated parent or one a ContentPresenter made), so screen readers skipped the title and the intro of
    /// every page with a PageHeader. The app's own controls show their texts through a ContentPresenter and a DataTemplate.
    /// </summary>
    [Fact]
    public void OwnControlsShowTheirTextsToScreenReaders()
    {
        var offenders = new List<string>();
        var templates = 0;
        foreach (var (relative, doc) in AppXaml())
            foreach (var template in doc.Descendants(Presentation + "ControlTemplate")
                         .Where(t => ((string?)t.Attribute("TargetType"))?.StartsWith("v:", StringComparison.Ordinal) == true))
            {
                templates++;
                foreach (var text in template.Descendants().Where(e => e.Name.LocalName == "TextBlock"
                             && !e.Ancestors().TakeWhile(a => a != template).Any(a => a.Name.LocalName == "DataTemplate")))
                    offenders.Add($"{relative}:{((IXmlLineInfo)text).LineNumber}: a TextBlock straight in the template of {(string?)template.Attribute("TargetType")} (show it through a ContentPresenter)");
            }
        Assert.Empty(offenders);
        Assert.True(templates >= 1, "the PageHeader template was not found");
    }

    /// <summary>The app's XAML files with forward-slash paths relative to the app project, line numbers kept.</summary>
    private static IEnumerable<(string Relative, XDocument Doc)> AppXaml() =>
        Directory.EnumerateFiles(RepoPaths.App, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(RepoPaths.App, f).Replace(Path.DirectorySeparatorChar, '/'), XDocument.Load(f, LoadOptions.SetLineInfo)));

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();

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
