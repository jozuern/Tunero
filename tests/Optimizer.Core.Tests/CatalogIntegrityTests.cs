using System.Text.RegularExpressions;
using Optimizer.Core.Actions;
using Optimizer.Core.Docs;
using Optimizer.Core.Tweaks;
using Xunit.Abstractions;

namespace Optimizer.Core.Tests;

/// <summary>
/// The catalog refers to other things by name: tweaks, findings, facts and labels. A typo there does not fail at load
/// time, it silently turns a rule into a no-op (a guard that never blocks, a reason that shows its key). These tests
/// check every reference.
/// </summary>
public partial class CatalogIntegrityTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<TweakDefinition> Tweaks = TweakCatalog.Current.Tweaks;

    private static IEnumerable<Condition> Conditions(TweakDefinition t) => t.Conditions();

    /// <summary>Fact names FactsBuilder writes, read from its source, plus the per-finding facts.</summary>
    private static readonly HashSet<string> KnownFacts = FactNamePattern()
        .Matches(File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Optimizer.Core", "Tweaks", "FactsBuilder.cs")))
        .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> FindingIds = DocLint.RequiredDocIds().ToHashSet(StringComparer.Ordinal);

    /// <summary>A condition compares with exactly one operator: {"gte": 1, "lte": 5} evaluated only "gte" and ignored the rest.</summary>
    [Fact]
    public void ConditionsUseOneOperatorEach()
    {
        static IEnumerable<Condition> Nodes(Condition c) =>
            new[] { c }.Concat((c.All ?? []).Concat(c.Any ?? []).Concat(c.Not is null ? [] : [c.Not]).SelectMany(Nodes));
        static int Operators(Condition c) =>
            new object?[] { c.Eq, c.Ne, c.Gt, c.Gte, c.Lt, c.Lte, c.In, c.Exists }.Count(o => o is not null)
            + (c.All is null ? 0 : 1) + (c.Any is null ? 0 : 1) + (c.Not is null ? 0 : 1);
        var bad = Tweaks.SelectMany(t => t.Conditions().SelectMany(Nodes).Where(n => n.Fact is not null ? Operators(n) > 1 : Operators(n) != 1)
            .Select(_ => t.Id)).Distinct().ToList();
        Assert.Empty(bad);
    }

    /// <summary>
    /// A proof names the file that holds the test result. A file that is no longer in docs/ is cited with the commit that
    /// has it ("docs/x.md in git 60c3a93^"); a plain path must exist, or the proof rule cannot be checked.
    /// </summary>
    [Fact]
    public void ProofFilesExist()
    {
        var missing = new List<string>();
        foreach (var t in Tweaks)
            foreach (Match m in t.Proof.SelectMany(p => Regex.Matches(p, @"docs/[\w./-]+\.md(?<git> in git [0-9a-f]{7,40}\^?)?")))
            {
                var path = m.Value.Split(' ')[0];
                if (!m.Groups["git"].Success && !File.Exists(Path.Combine(RepoPaths.Root, path))) missing.Add($"{t.Id}: {path}");
            }
        Assert.Empty(missing);
    }

    /// <summary>
    /// The data files are read leniently (maintainer "_comment" keys); a misspelled property would be ignored and turn a
    /// rule into a no-op ("recomendWhen" in profiles.json). Without the comments, every property must map.
    /// </summary>
    [Fact]
    public void DataFilesHaveNoUnknownProperties()
    {
        var types = new Dictionary<string, Type>
        {
            ["appx.json"] = typeof(Debloat.AppxCatalog), ["services.json"] = typeof(Services.ServiceCatalog), ["apps.json"] = typeof(Apps.AppCatalog),
            ["features.json"] = typeof(Tools.FeatureCatalog), ["profiles.json"] = typeof(Profiles.ProfileCatalog), ["gpu.json"] = typeof(Catalog.GpuCatalog),
            ["cpu.json"] = typeof(Catalog.CpuCatalog), ["ram.json"] = typeof(Catalog.RamCatalog), ["anticheat.json"] = typeof(Catalog.AntiCheatCatalog),
            ["bios.json"] = typeof(Catalog.BiosCatalog), ["storage.json"] = typeof(Catalog.StorageCatalog), ["extras.json"] = typeof(Catalog.ExtrasCatalog),
            ["notincluded.json"] = typeof(Catalog.NotIncludedCatalog), ["mediaimages.json"] = typeof(Media.MediaImageCatalog), ["mediaparts.json"] = typeof(Media.MediaPartCatalog),
        };
        var files = Catalog.CatalogData.ResourceNames("Catalog.Data.").Select(n => n["Catalog.Data.".Length..]).Where(n => n != "labels.json").ToList();
        Assert.Equal(files.Order(), types.Keys.Order());
        var strict = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        static void StripComments(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject o)
            {
                o.Remove("_comment");
                foreach (var (_, child) in o) StripComments(child);
            }
            else if (node is System.Text.Json.Nodes.JsonArray a)
            {
                foreach (var child in a) StripComments(child);
            }
        }
        var errors = new List<string>();
        foreach (var (file, type) in types)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Catalog.CatalogData.ReadResourceText("Catalog.Data." + file),
                documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            StripComments(node);
            try
            {
                System.Text.Json.JsonSerializer.Deserialize(node, type, strict);
            }
            catch (System.Text.Json.JsonException ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }
        foreach (var e in errors) output.WriteLine(e);
        Assert.Empty(errors);
    }

    private static bool IsKnownFact(string fact)
    {
        if (KnownFacts.Contains(fact)) return true;
        var m = FindingFactPattern().Match(fact);
        return m.Success && FindingIds.Contains(m.Groups[1].Value);
    }

    private void AssertNone(List<string> problems)
    {
        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }

    [Fact]
    public void CatalogFilesHaveNoDuplicateKeys()
    {
        // System.Text.Json keeps the last of two equal keys without a word; a merge once left two "subject" keys per DNS preset.
        var problems = new List<string>();
        var catalog = Path.Combine(RepoPaths.Root, "src", "Optimizer.Core", "Catalog");
        var options = new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (var file in Directory.EnumerateFiles(catalog, "*.json", SearchOption.AllDirectories))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file), options);
            void Walk(System.Text.Json.JsonElement e, string at)
            {
                if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    var here = e.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString()! : at;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in e.EnumerateObject())
                    {
                        if (!seen.Add(p.Name)) problems.Add($"{Path.GetFileName(file)} {here}: \"{p.Name}\" twice");
                        Walk(p.Value, here);
                    }
                }
                else if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var item in e.EnumerateArray()) Walk(item, at);
            }
            Walk(doc.RootElement, "(root)");
        }
        AssertNone(problems);
    }

    [Fact]
    public void TweaksSharingAPageHaveDistinctSubjects()
    {
        // The title comes from the page, so without a subject the rows read the same (the DNS presets in the VM test).
        AssertNone(Tweaks.GroupBy(t => t.DocId).Where(g => g.Count() > 1)
            .SelectMany(g => g.GroupBy(t => t.Subject ?? "").Where(s => s.Count() > 1)
                .Select(s => $"{g.Key}: {string.Join(", ", s.Select(t => t.Id))} share the title, set \"subject\""))
            .ToList());
    }

    [Fact]
    public void TweaksWritingTheSameTargetConflict()
    {
        // Undoing one of two active tweaks on one target makes the other read as "reset by Windows": the conflict check
        // by shared target keeps only one of them active, without a declaration. This rule, written independently of
        // TweakAction.SharedTargets, finds the pairs that the check must catch.
        // A power setting restores only the side it writes, and a network adapter action only its own keywords.
        static IEnumerable<string> Targets(TweakAction a) => a switch
        {
            PowerSettingAction p => new[] { p.Ac is null ? null : "ac", p.Dc is null ? null : "dc" }.OfType<string>().Select(side => $"{a.TargetKey}:{side}"),
            NicPropertyAction n => n.Properties.Keys.Select(k => $"nic:{n.Media}:{k}".ToLowerInvariant()),
            _ => [a.TargetKey],
        };
        var problems = new List<string>();
        foreach (var g in Tweaks.SelectMany(t => t.Actions.SelectMany(Targets).Select(key => (Tweak: t, TargetKey: key))).GroupBy(x => x.TargetKey))
        {
            var sharing = g.Select(x => x.Tweak).DistinctBy(t => t.Id).ToList();
            for (var i = 0; i < sharing.Count; i++)
                for (var j = i + 1; j < sharing.Count; j++)
                    if (!TargetOverlap.SharesTarget(sharing[i], sharing[j]) || !TargetOverlap.Conflict(sharing[j], sharing[i]))
                        problems.Add($"{sharing[i].Id} and {sharing[j].Id} both write {g.Key}, but the conflict check misses it");
        }
        AssertNone(problems);
    }

    [Fact]
    public void ReferencedTweaksAndFindingsExist()
    {
        // Catalog tweaks and the fixes built at run time (fix.powerMode and the like).
        var ids = Tweaks.Select(t => t.Id).Concat(Findings.Checks.RuntimeFixes.DocIds).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var t in Tweaks)
        {
            problems.AddRange(t.ConflictsWith.Where(c => !ids.Contains(c)).Select(c => $"{t.Id}: conflicts with unknown tweak {c}"));
            problems.AddRange(t.Fixes.Where(f => !FindingIds.Contains(f)).Select(f => $"{t.Id}: fixes unknown finding {f}"));
        }
        AssertNone(problems);
    }

    [Fact]
    public void ConditionsUseFactsTheAppComputes()
    {
        Assert.True(KnownFacts.Count > 20, "FactsBuilder.cs was not parsed");
        var problems = Tweaks.SelectMany(t => Conditions(t).SelectMany(c => c.ReferencedFacts()).Where(f => !IsKnownFact(f)).Select(f => $"{t.Id}: unknown fact {f}")).ToList();
        foreach (var p in CatalogDataProfilesFacts()) problems.Add(p);
        AssertNone(problems);
    }

    private static IEnumerable<string> CatalogDataProfilesFacts() =>
        Catalog.CatalogData.Current.Profiles.Profiles.SelectMany(p =>
            (p.SuggestWhen?.ReferencedFacts() ?? []).Concat(p.Tweaks.SelectMany(pt => pt.RecommendWhen?.ReferencedFacts() ?? []))
                .Where(f => !IsKnownFact(f)).Select(f => $"profile {p.Id}: unknown fact {f}"));

    [Fact]
    public void LabelKeysExistInBothLanguages()
    {
        var keys = Tweaks.SelectMany(t =>
                new[] { t.RecommendReasonKey }
                    .Concat(t.ImpactOverrides.Select(o => o.ReasonKey))
                    .Concat(Conditions(t).Select(c => c.ReasonKey))
                    .Append($"category.{t.Category}")
                    .Concat(t.Impact.Effect.Select(e => $"effect.{e}")))
            .OfType<string>().Distinct();
        var problems = keys.SelectMany(k => DocStore.Languages.Where(l => !Labels.Current.Has(l, k)).Select(l => $"[{l}] missing label {k}")).ToList();
        AssertNone(problems);
    }

    [Fact]
    public void FreeTextFieldsUseKnownValues()
    {
        string[] registryKinds = ["dword", "qword", "string", "expandString", "multiString", "binary"];
        var problems = new List<string>();
        foreach (var t in Tweaks)
        {
            if (t.AppliesTo.FormFactor is not ("any" or "desktop" or "laptop")) problems.Add($"{t.Id}: formFactor {t.AppliesTo.FormFactor}");
            if (t.Verify is not ("actions" or "afterRestart")) problems.Add($"{t.Id}: verify {t.Verify}");
            foreach (var r in t.Actions.OfType<RegistryAction>().Where(r => !registryKinds.Contains(r.Kind))) problems.Add($"{t.Id}: registry kind {r.Kind}");
            foreach (var v in t.AppliesTo.GpuVendor ?? []) if (v is not ("nvidia" or "amd" or "intel")) problems.Add($"{t.Id}: gpuVendor {v}");
            foreach (var v in t.AppliesTo.CpuVendor ?? []) if (v is not ("intel" or "amd")) problems.Add($"{t.Id}: cpuVendor {v}");
        }
        AssertNone(problems);
    }

    /// <summary>Every category of the catalog belongs to a section of the Tweaks page (a new one would land in Security silently).</summary>
    [Fact]
    public void EveryCategoryHasATweaksPageSection()
    {
        var missing = TweakCatalog.Current.Tweaks.Select(t => t.Category).Distinct().Where(c => !TweakSections.Contains(c)).ToList();
        Assert.Empty(missing);
        Assert.Equal(TweakSections.All.Count, TweakSections.All.Select(s => s.Key).Distinct().Count());
    }

    /// <summary>Unknown facts never match, also under "not": an edition-gated policy is not offered when the edition is unknown.</summary>
    [Fact]
    public void NotOfAnUnknownFactDoesNotMatch()
    {
        var notHome = new Condition { Not = new Condition { Fact = "os.edition", In = [System.Text.Json.JsonSerializer.SerializeToElement("Core")] } };
        Assert.True(notHome.Evaluate(new Facts().Set("os.edition", "Professional")));
        Assert.False(notHome.Evaluate(new Facts().Set("os.edition", "Core")));
        Assert.False(notHome.Evaluate(new Facts()));
        // "exists" is about a missing fact and stays known.
        Assert.True(new Condition { Not = new Condition { Fact = "os.edition", Exists = true } }.Evaluate(new Facts()));
    }

    [Fact]
    public void AGuardThatCannotBeCheckedBlocks()
    {
        var guard = new Condition { Fact = "power.modernStandby", Eq = System.Text.Json.JsonSerializer.SerializeToElement(true), ReasonKey = "block.modernStandby" };
        var t = new TweakDefinition
        {
            Id = "test.guarded", Category = "Test", Hidden = true, Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
            Actions = [], BlockedWhen = [guard],
        };
        using var fx = new EngineFixture();
        var known = new Facts().Set("os.build", 26300).Set("elevated", true).Set("power.modernStandby", false);
        Assert.Empty(fx.Engine.Preflight(t, known, new ApplyOptions { ExpertMode = true }));
        var unknown = new Facts().Set("os.build", 26300).Set("elevated", true); // the power probe failed
        Assert.Contains(fx.Engine.Preflight(t, unknown, new ApplyOptions { ExpertMode = true }), b => b.ReasonKey == "block.cannotCheck");
    }

    [GeneratedRegex(@"\.Set\(""([\w.]+)""")]
    private static partial Regex FactNamePattern();

    [GeneratedRegex(@"^finding\.([\w.]+?)\.(problem|status)$")]
    private static partial Regex FindingFactPattern();
}
