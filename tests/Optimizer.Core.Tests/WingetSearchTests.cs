using Optimizer.Core.Apps;

namespace Optimizer.Core.Tests;

/// <summary>Searching winget for any package: a safe query, and the table winget prints read in any display language.</summary>
public class WingetSearchTests
{
    // Printed by winget 1.29 in the VM for "notepad" (lines shortened to a few).
    private const string English = """
        Name                 Id                             Version    Match
        ---------------------------------------------------------------------------------------
        AkelPad              AkelPad.AkelPad                4.9.9      Tag: notepad
        NoteTab Light        FookesHolding.NoteTabLight     7.2        Tag: notepad
        Notepad++            Notepad++.Notepad++            8.9.8.1    Tag: notepad
        Programmer's Notepad SimonSteele.ProgrammersNotepad 2.4.2
        Notepad4 (AVX2)      zufuliu.notepad4.AVX2          26.08r6282
        """;

    [Fact]
    public void ResultsAreReadByColumnPosition()
    {
        var found = Winget.ParseSearch(English);
        Assert.Equal(5, found.Count);
        Assert.Equal(new WingetSearchResult("NoteTab Light", "FookesHolding.NoteTabLight", "7.2"), found[1]);
        Assert.Equal(new WingetSearchResult("Notepad++", "Notepad++.Notepad++", "8.9.8.1"), found[2]);
        Assert.Equal(new WingetSearchResult("Programmer's Notepad", "SimonSteele.ProgrammersNotepad", "2.4.2"), found[3]);
        Assert.Equal("26.08r6282", found[4].Version);
    }

    /// <summary>winget pads by display width: a name in Chinese takes two cells per character, the columns after it stay in place.</summary>
    [Fact]
    public void WideCharactersCountTwoCells()
    {
        const string table = "Name         Id                 Version\n" +
                             "---------------------------------------\n" +
                             "微信         Tencent.WeChat     4.0.6\n" +
                             "7-Zip        7zip.7zip          26.04\n";
        var found = Winget.ParseSearch(table);
        Assert.Equal(new WingetSearchResult("微信", "Tencent.WeChat", "4.0.6"), found[0]);
        Assert.Equal(new WingetSearchResult("7-Zip", "7zip.7zip", "26.04"), found[1]);
    }

    [Fact]
    public void TranslatedHeadersSpinnersShortenedIdsAndNotesAreHandled()
    {
        const string german = "\r   - \r   \\ \rName      ID                 Version Übereinstimmung Quelle\n" +
                              "-------------------------------------------------------------\n" +
                              "7-Zip     7zip.7zip          26.04                   winget\n" +
                              "Long app  Vendor.VeryLongPac\u2026 1.0                   winget\n" +
                              "Weitere Ergebnisse sind verfügbar\n";
        var found = Winget.ParseSearch(german);
        Assert.Equal(new WingetSearchResult("7-Zip", "7zip.7zip", "26.04"), Assert.Single(found));
        Assert.Empty(Winget.ParseSearch("No package found matching input criteria."));
        Assert.Empty(Winget.ParseSearch(""));
    }

    // Printed by winget 1.29 in PCO-Test (2026-10-21) for "upgrade --source winget" with 7-Zip 23.01 installed; the
    // second table and the note are the shape winget gives pinned packages and unknown versions.
    private const string Upgrades = """
        Name                      Id        Version    Available
        --------------------------------------------------------
        7-Zip 23.01 (x64 edition) 7zip.7zip 23.01.00.0 26.04
        1 upgrades available.

        The following packages have an upgrade available, but require explicit targeting for upgrade:
        Name      Id             Version Available
        ------------------------------------------
        Contoso   Contoso.Pinned 1.0     2.0
        """;

    /// <summary>
    /// The first table only, also as IProcessRunner.RunAsync hands the lines over (no empty lines, so the pinned table follows
    /// the note directly); a longer translated note under it is no package.
    /// </summary>
    [Fact]
    public void UpgradesAreReadByColumnPosition()
    {
        var expected = new WingetUpgrade("7-Zip 23.01 (x64 edition)", "7zip.7zip", "23.01.00.0", "26.04");
        Assert.Equal([expected], Winget.ParseUpgrades(Upgrades));
        Assert.Equal([expected], Winget.ParseUpgrades(string.Join("\n", Upgrades.Split('\n').Where(l => l.Trim().Length > 0))));
        Assert.Equal([expected], Winget.ParseUpgrades(Upgrades.Replace("1 upgrades available.", "1 Aktualisierungen verfügbar.")));
        Assert.Empty(Winget.ParseUpgrades("No installed package found matching input criteria."));
        Assert.Equal("upgrade --source winget --accept-source-agreements --disable-interactivity", Winget.UpgradesArguments);
    }

    [Theory]
    [InlineData("notepad", true)]
    [InlineData("Visual Studio Code", true)]
    [InlineData("C++ Redist", true)]
    [InlineData("Übersetzer", true)]
    [InlineData("a", false)]
    [InlineData("x\" --source evil", false)]
    [InlineData("a & calc", false)]
    [InlineData("a|b", false)]
    public void QueriesAreLimitedToSafeCharacters(string query, bool ok)
    {
        Assert.Equal(ok, Winget.IsSafeQuery(query));
        if (ok) Assert.Equal($"search --query \"{query}\" --source winget --count 50 --accept-source-agreements --disable-interactivity", Winget.SearchArguments(query));
        else Assert.Throws<ArgumentException>(() => Winget.SearchArguments(query));
    }
}
