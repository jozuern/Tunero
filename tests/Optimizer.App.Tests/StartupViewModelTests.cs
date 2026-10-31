using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Actions;
using Optimizer.Core.Docs;
using Optimizer.Core.Startup;

namespace Optimizer.App.Tests;

/// <summary>
/// The Startup page's tabs without the window or a scan: which entry lands on which tab, the counts in the headers under
/// the search and filters, and the summary for the open tab. The rows are built from hand-made entries with their
/// signatures set; nothing is read from or changed on this PC.
/// </summary>
public sealed class StartupViewModelTests
{
    private static readonly SignatureInfo Microsoft = new(SignatureStatus.Signed, "Microsoft Windows", Catalog: true, MicrosoftRoot: true);
    private static readonly SignatureInfo Vendor = new(SignatureStatus.Signed, "Valve Corp.", Catalog: false, MicrosoftRoot: false);
    private static readonly SignatureInfo Unsigned = new(SignatureStatus.Unsigned, null, Catalog: false);

    private static StartupEntry Entry(StartupKind kind, string name, bool? enabled = true, bool startsWithWindows = true) =>
        new(kind, name, $@"C:\Programs\{name}.exe", $@"C:\Programs\{name}.exe", $@"HKCU\Test\{kind}", Hive.User, enabled, $"{kind}:{name}")
        {
            StartsWithWindows = startsWithWindows,
            // What the scanner stores for the switch: the approved key, the task's path, the service or the CLSID.
            Target = kind switch
            {
                StartupKind.RunKey => @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                StartupKind.StartupFolder => $@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder|{name}.lnk",
                StartupKind.LogonTask => $@"\Vendor\{name}",
                StartupKind.ShellExtension => "{00000000-0000-0000-0000-000000000001}",
                _ => name.Replace(" ", "", StringComparison.Ordinal),
            },
        };

    private static StartupRow Row(StartupEntry entry, SignatureInfo signature)
    {
        var row = new StartupRow(entry, Loc.Instance.Language, null!, null!);
        row.SetSignature(signature, runsScriptHost: false, Loc.Instance.Language);
        return row;
    }

    /// <summary>
    /// Apps at sign-in: two Run entries (one of Windows, one not signed and turned off), a Startup folder item and a task
    /// at sign-in. Scheduled tasks: a daily task. Services: one of a vendor, one of Windows. Drivers: one. Advanced: an
    /// Explorer extension and a Winsock provider.
    /// </summary>
    private static StartupViewModel Page()
    {
        var vm = new StartupViewModel(new FakePageHost(), null!, null!);
        vm.ShowRows(
        [
            Row(Entry(StartupKind.RunKey, "OneDrive"), Microsoft),
            Row(Entry(StartupKind.RunKey, "Steam", enabled: false), Unsigned),
            Row(Entry(StartupKind.StartupFolder, "Discord"), Vendor),
            Row(Entry(StartupKind.LogonTask, "Vendor Logon Task"), Vendor),
            Row(Entry(StartupKind.LogonTask, "Daily Updater", startsWithWindows: false), Vendor),
            Row(Entry(StartupKind.Service, "Vendor Service"), Vendor),
            Row(Entry(StartupKind.Service, "Windows Service"), Microsoft),
            Row(Entry(StartupKind.Driver, "Vendor Driver", enabled: null), Vendor),
            Row(Entry(StartupKind.ShellExtension, "Archive Menu"), Vendor),
            Row(Entry(StartupKind.WinsockProvider, "Proxy Layer", enabled: null), Unsigned),
        ]);
        return vm;
    }

    private static int[] Counts(StartupViewModel vm) =>
        [.. vm.Tabs.Select(t => int.Parse(t.Header[(t.Header.LastIndexOf('(') + 1)..^1], System.Globalization.CultureInfo.InvariantCulture))];

    [Fact]
    public void EveryKindLandsOnItsTab()
    {
        StartupTab Tab(StartupKind kind, bool startsWithWindows = true) => StartupViewModel.TabOf(Entry(kind, "x", startsWithWindows: startsWithWindows));

        foreach (var kind in new[] { StartupKind.RunKey, StartupKind.StartupFolder, StartupKind.PolicyRun, StartupKind.RunOnce })
            Assert.Equal(StartupTab.SignIn, Tab(kind));
        Assert.Equal(StartupTab.SignIn, Tab(StartupKind.LogonTask));
        Assert.Equal(StartupTab.Tasks, Tab(StartupKind.LogonTask, startsWithWindows: false));
        Assert.Equal(StartupTab.Services, Tab(StartupKind.Service));
        Assert.Equal(StartupTab.Drivers, Tab(StartupKind.Driver));
        // Every other location Autoruns lists is under Advanced.
        var named = new[] { StartupKind.RunKey, StartupKind.StartupFolder, StartupKind.PolicyRun, StartupKind.RunOnce, StartupKind.LogonTask, StartupKind.Service, StartupKind.Driver };
        foreach (var kind in Enum.GetValues<StartupKind>().Except(named))
            Assert.Equal(StartupTab.Advanced, Tab(kind));
    }

    [Fact]
    public void HeadersCountWhatEachTabShowsUnderTheFilters()
    {
        var vm = Page();
        // Show Windows entries is off: OneDrive and the Windows service are left out.
        Assert.Equal([3, 1, 1, 1, 2], Counts(vm));
        Assert.StartsWith(StartupViewModel.TabName(StartupTab.SignIn), vm.Tabs[0].Header, StringComparison.Ordinal);

        vm.ShowMicrosoft = true;
        Assert.Equal([4, 1, 2, 1, 2], Counts(vm));

        vm.OnlyAttention = true;
        Assert.Equal([1, 0, 0, 0, 1], Counts(vm));

        vm.OnlyAttention = false;
        vm.Search = "vendor";
        Assert.Equal([1, 0, 1, 1, 0], Counts(vm));
    }

    [Fact]
    public void TheOpenTabListsOnlyItsEntries()
    {
        var vm = Page();
        Assert.Equal(["Steam", "Discord", "Vendor Logon Task"], vm.Rows.Select(r => r.Name));
        Assert.False(vm.ShowServicesHint);

        vm.SelectedTab = (int)StartupTab.Tasks;
        Assert.Equal(["Daily Updater"], vm.Rows.Select(r => r.Name));
        Assert.Equal(1, vm.ServicesHintTab);

        vm.SelectedTab = (int)StartupTab.Services;
        Assert.Equal(["Vendor Service"], vm.Rows.Select(r => r.Name));
        Assert.Equal(0, vm.ServicesHintTab);

        vm.SelectedTab = (int)StartupTab.Drivers;
        Assert.Equal(["Vendor Driver"], vm.Rows.Select(r => r.Name));
        Assert.False(vm.ShowServicesHint);
        Assert.False(vm.IsAdvancedTab);
    }

    [Fact]
    public void TheAdvancedTabFiltersByTheKindsFound()
    {
        var vm = Page();
        vm.SelectedTab = (int)StartupTab.Advanced;
        Assert.True(vm.IsAdvancedTab);
        // All, then only the kinds this PC has.
        Assert.Equal(["", nameof(StartupKind.ShellExtension), nameof(StartupKind.WinsockProvider)], vm.AdvancedKinds.Select(k => k.Key).OrderBy(k => k));
        Assert.Equal("", vm.SelectedAdvancedKind?.Key);
        Assert.Equal(2, vm.Rows.Count);

        vm.SelectedAdvancedKind = vm.AdvancedKinds.Single(k => k.Key == nameof(StartupKind.WinsockProvider));
        Assert.Equal(["Proxy Layer"], vm.Rows.Select(r => r.Name));
        // The header counts the whole tab, not the chosen kind.
        Assert.Equal(2, Counts(vm)[(int)StartupTab.Advanced]);

        // The kind stays chosen when the rows are built again (a reload or a language switch).
        vm.ShowRows([Row(Entry(StartupKind.WinsockProvider, "Proxy Layer", enabled: null), Unsigned), Row(Entry(StartupKind.Codec, "Codec"), Vendor)]);
        Assert.Equal(nameof(StartupKind.WinsockProvider), vm.SelectedAdvancedKind?.Key);
        Assert.Equal(["Proxy Layer"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public void TheSummarySpeaksForTheOpenTab()
    {
        var vm = Page();
        // Apps at sign-in: four, Steam turned off and not signed, OneDrive hidden as a Windows entry.
        Assert.Equal(StartupViewModel.Summary(StartupTab.SignIn, 4, 1, 1, 1, checking: false), vm.SummaryText);
        // It leads with what the list shows, the number in the tab's header, and names the total only there.
        Assert.Equal(string.Join(" ",
            Loc.Instance.Format("Startup_Showing", 3, 4),
            Loc.Instance.Format("Startup_OffOne", 1),
            Loc.Instance.Format("Startup_AttentionOne", 1)), vm.SummaryText);
        Assert.Equal(3, Counts(vm)[(int)StartupTab.SignIn]);

        vm.SelectedTab = (int)StartupTab.Drivers;
        Assert.Equal(Loc.Instance.Format("Startup_SummaryCount", Loc.Instance.Format("Startup_CountDriversOne", 1)), vm.SummaryText);
    }

    [Fact]
    public void TheSummaryHasOneAndManyFormsAndWaitsForSignatures()
    {
        string Count(string key, int n) => Loc.Instance.Format(key, n);

        Assert.Equal(Loc.Instance.Format("Startup_SummaryCount", Count("Startup_CountServicesOne", 1)),
            StartupViewModel.Summary(StartupTab.Services, 1, 0, 0, 0, checking: false));
        Assert.Equal(Loc.Instance.Format("Startup_SummaryCountOff", Count("Startup_CountServicesOne", 1)),
            StartupViewModel.Summary(StartupTab.Services, 1, 1, 0, 0, checking: false));
        Assert.Equal(string.Join(" ", Loc.Instance.Format("Startup_SummaryCountSomeOff", Count("Startup_CountTasks", 12), 4), Count("Startup_Attention", 2)),
            StartupViewModel.Summary(StartupTab.Tasks, 12, 4, 2, 0, checking: false));
        Assert.Equal(Loc.Instance.Format("Startup_SummaryCount", Count("Startup_CountAdvanced", 0)),
            StartupViewModel.Summary(StartupTab.Advanced, 0, 0, 0, 0, checking: false));
        // With some hidden, the total is said once ("Showing 4 of 51."), followed by how many are off.
        Assert.Equal(string.Join(" ", Loc.Instance.Format("Startup_Showing", 4, 51), Count("Startup_Off", 16), Count("Startup_AttentionOne", 1)),
            StartupViewModel.Summary(StartupTab.SignIn, 51, 16, 1, 47, checking: false));
        Assert.Equal(Loc.Instance.Format("Startup_Showing", 4, 51), StartupViewModel.Summary(StartupTab.SignIn, 51, 0, 0, 47, checking: false));
        // While the signatures are checked, what needs attention and what is hidden are not counted yet.
        Assert.Equal(string.Join(" ", Loc.Instance.Format("Startup_SummaryCount", Count("Startup_CountSignIn", 5)), Loc.Instance["Startup_SummaryChecking"]),
            StartupViewModel.Summary(StartupTab.SignIn, 5, 0, 2, 3, checking: true));
    }

    [Fact]
    public void NothingMatchesOnlyWhenTheTabHasEntries()
    {
        var vm = Page();
        vm.Search = "no such entry";
        Assert.True(vm.NoMatch);

        // A tab without entries says so in its summary, not "nothing matches".
        vm.Search = "";
        vm.ShowRows([Row(Entry(StartupKind.RunKey, "Steam"), Vendor)]);
        vm.SelectedTab = (int)StartupTab.Drivers;
        Assert.Empty(vm.Rows);
        Assert.False(vm.NoMatch);
    }

    [Fact]
    public void ASearchResultOpensTheTabOfItsEntry()
    {
        var vm = Page();
        vm.OnlyAttention = true;
        vm.Reveal("Daily Updater");
        Assert.Equal((int)StartupTab.Tasks, vm.SelectedTab);
        Assert.True(vm.ShowMicrosoft);
        Assert.False(vm.OnlyAttention);
        Assert.Equal(["Daily Updater"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public void TheSimpleViewNamesTheKindInPlainWords()
    {
        Assert.Equal(Loc.Instance["Startup_PlainRegistry"], Row(Entry(StartupKind.RunKey, "Steam"), Unsigned).SimpleKindText);
        Assert.Equal(Loc.Instance["Startup_PlainTask"], Row(Entry(StartupKind.LogonTask, "Updater"), Vendor).SimpleKindText);
        // A tab of one kind does not repeat it on every card.
        Assert.Equal("", Row(Entry(StartupKind.LogonTask, "Updater", startsWithWindows: false), Vendor).SimpleKindText);
        Assert.Equal("", Row(Entry(StartupKind.Service, "Vendor Service"), Vendor).SimpleKindText);
        var winsock = Row(Entry(StartupKind.WinsockProvider, "Proxy Layer", enabled: null), Vendor);
        Assert.Equal(winsock.KindText, winsock.SimpleKindText);
        Assert.StartsWith(winsock.KindText, winsock.MetaText, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsEntriesWithoutTheirFileAreWindowsEntries()
    {
        var notFound = new SignatureInfo(SignatureStatus.NotFound, null, Catalog: false);
        StartupRow Missing(StartupKind kind, string? target, Hive hive = Hive.Machine, string? command = null)
        {
            var row = new StartupRow(new StartupEntry(kind, "x", command, null, "HKLM", hive, null, $"{kind}:x") { Target = target }, Loc.Instance.Language, null!, null!);
            row.SetSignature(notFound, runsScriptHost: false, Loc.Instance.Language);
            return row;
        }

        Assert.True(Missing(StartupKind.LogonTask, @"\Microsoft\Windows\LanguageComponentsInstaller\ReconcileLanguageResources").IsPlainMicrosoft);
        Assert.True(Missing(StartupKind.KnownDll, "_wowarmhw").IsPlainMicrosoft);
        Assert.True(Missing(StartupKind.ActiveSetup, "{89820200-ECBD-11cf-8B85-00AA005B4383}", command: "U").IsPlainMicrosoft);
        // Another program's missing file still shows: a task outside Windows' folder, an Active Setup stub with a path.
        Assert.False(Missing(StartupKind.LogonTask, @"\Vendor\Updater").IsPlainMicrosoft);
        Assert.False(Missing(StartupKind.ActiveSetup, "{1}", command: @"C:\Program Files\Vendor\setup.exe /user").IsPlainMicrosoft);
        Assert.False(Missing(StartupKind.ActiveSetup, "{1}", Hive.User, command: "U").IsPlainMicrosoft);
        Assert.False(Missing(StartupKind.RunKey, "x").IsPlainMicrosoft);
        // Windows' own shell service object WebCheck without a COM server, recognized by place and CLSID: under another
        // CLSID, in another place or with a server file the name alone does not make it Windows'.
        const string ssodl = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad";
        const string webCheck = "{E6FB5E20-DE35-11CF-9C87-00AA005127ED}";
        StartupRow AddOn(string clsid, string location, string? dll = null)
        {
            var row = new StartupRow(new StartupEntry(StartupKind.ExplorerAddOn, "Shell service object: WebCheck", dll ?? clsid, dll, location, Hive.Machine, null,
                $"exploreraddon:Shell service object:{clsid}"), Loc.Instance.Language, null!, null!);
            row.SetSignature(notFound, runsScriptHost: false, Loc.Instance.Language);
            return row;
        }
        Assert.True(AddOn(webCheck, ssodl).IsPlainMicrosoft);
        Assert.False(AddOn("{11111111-2222-3333-4444-555555555555}", ssodl).IsPlainMicrosoft);
        Assert.False(AddOn(webCheck, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler").IsPlainMicrosoft);
        Assert.False(AddOn(webCheck, ssodl, @"C:\Users\x\AppData\Local\Temp\webcheck.dll").IsPlainMicrosoft);
        // A Windows file is Microsoft's whichever of Windows' own signers signed it, the HAL publisher too.
        var hal = new SignatureInfo(SignatureStatus.Signed, "Microsoft Windows Hardware Abstraction Layer Publisher", Catalog: false, MicrosoftRoot: true);
        Assert.True(Row(Entry(StartupKind.Driver, "intelpep", enabled: null), hal).IsPlainMicrosoft);
        var whql = new SignatureInfo(SignatureStatus.Signed, "Microsoft Windows Hardware Compatibility Publisher", Catalog: false, MicrosoftRoot: true);
        Assert.False(Row(Entry(StartupKind.Driver, "vendor", enabled: null), whql).IsPlainMicrosoft);
    }

    [Fact]
    public void AServiceSwitchNamesTheStartTypesItSets()
    {
        var service = Row(Entry(StartupKind.Service, "Vendor Service"), Vendor);
        Assert.Equal(Labels.Current.Get(Loc.Instance.Language, "serviceStart.Automatic"), service.OnText);
        Assert.Equal(Labels.Current.Get(Loc.Instance.Language, "serviceStart.Manual"), service.OffText);
        Assert.Equal(Loc.Instance["Startup_ServiceSwitchTip"], service.SwitchTip);
        var run = Row(Entry(StartupKind.RunKey, "Steam"), Vendor);
        Assert.Equal(Loc.Instance["Startup_Enabled"], run.OnText);
        Assert.Equal(Loc.Instance["Startup_Disabled"], run.OffText);
        Assert.Null(run.SwitchTip);
    }

    [Fact]
    public void ATaskOnAppsAtSignInNamesItsTrigger()
    {
        Assert.Equal(Loc.Instance["Startup_PlainTask"], Row(Entry(StartupKind.LogonTask, "Logon"), Vendor).KindText);
        var boot = Row(Entry(StartupKind.LogonTask, "Boot") with { AtBootOnly = true }, Vendor);
        Assert.Equal(Loc.Instance["Startup_PlainTaskAtBoot"], boot.KindText);
        Assert.Equal(Loc.Instance["Startup_PlainTaskAtBoot"], boot.SimpleKindText);
        // On the tab of the other tasks the kind is the technical name.
        Assert.Equal(Labels.Current.Get(Loc.Instance.Language, "startupKind.LogonTask"), Row(Entry(StartupKind.LogonTask, "Daily", startsWithWindows: false), Vendor).KindText);
    }

    [Fact]
    public void TheHintUnderTheListFitsTheTab()
    {
        var vm = Page();
        vm.SelectedTab = (int)StartupTab.Tasks;
        Assert.Equal(Loc.Instance["Startup_TasksHint"], vm.ServicesHintText);
        vm.SelectedTab = (int)StartupTab.Services;
        Assert.Equal(Loc.Instance["Startup_ServicesHint"], vm.ServicesHintText);
    }

    [Fact]
    public void TheExportNamesEverySignature()
    {
        var unsigned = Row(Entry(StartupKind.RunKey, "Steam"), Unsigned);
        // On the card the tag says it; the export has no tags column, so its signature column does.
        Assert.Equal("", unsigned.SignatureText);
        Assert.Equal(Labels.Current.Get(Loc.Instance.Language, "signature.Unsigned"), unsigned.ExportSignatureText);
        Assert.Equal(Labels.Current.Get(Loc.Instance.Language, "signature.Signed"), Row(Entry(StartupKind.RunKey, "Discord"), Vendor).ExportSignatureText);
    }

    [Theory]
    [InlineData("Bluetooth Support Service: for Bluetooth controllers.", "Bluetooth Support Service", "For Bluetooth controllers.")]
    [InlineData("bluetooth support service: for Bluetooth controllers.", "Bluetooth Support Service", "For Bluetooth controllers.")]
    // Hyphens, spaces and case may differ between the catalog and Windows; other words still count as another name.
    [InlineData("Xbox Live-Authentifizierungs-Manager: für Anmeldungen bei Xbox Live.", "Xbox Live Authentifizierungs-Manager", "Für Anmeldungen bei Xbox Live.")]
    [InlineData("Xbox Live Auth Manager: for Xbox Live sign-ins.", "Xbox Live Auth-Manager", "For Xbox Live sign-ins.")]
    [InlineData("Netzwerkservice: für das Netzwerk.", "Netzwerkdienst", "Netzwerkservice: für das Netzwerk.")]
    [InlineData("Xbox: Live Manager: x.", "Xbox Live Manager", "Xbox: Live Manager: x.")]
    [InlineData("Print Spooler: needed for printing.", "Druckwarteschlange", "Print Spooler: needed for printing.")]
    [InlineData("Bluetooth Support Service", "Bluetooth Support Service", "Bluetooth Support Service")]
    [InlineData(null, "x", null)]
    public void AServiceNoteDoesNotRepeatTheTitle(string? note, string displayName, string? expected) =>
        Assert.Equal(expected, ServiceRowVm.WithoutName(note, displayName));
}
