using Optimizer.Core.Catalog;
using Optimizer.Core.Media;
using Optimizer.Core.Tools;

namespace Optimizer.Core.Tests;

/// <summary>mediaparts.json: the optional Windows parts the install media page can remove. Reads the embedded catalog only.</summary>
public sealed class MediaPartsCatalogTests
{
    private static IReadOnlyList<MediaPart> Parts => CatalogData.Current.MediaParts;

    [Fact]
    public void ThereAreParts() => Assert.NotEmpty(Parts);

    [Fact]
    public void EveryPartHasBothTextsASourceAndSafeNames()
    {
        foreach (var part in Parts)
        {
            Assert.Matches("^[a-z][A-Za-z0-9]*$", part.Id);
            Assert.NotEmpty(part.Title);
            Assert.NotEmpty(part.TitleDe);
            Assert.InRange(part.En.Length, 20, 200);
            Assert.InRange(part.De.Length, 20, 200);
            Assert.StartsWith("https://learn.microsoft.com/", part.Source, StringComparison.Ordinal);
            Assert.NotEmpty(part.Names);
            Assert.Contains(part.Kind, new[] { "capability", "feature" });
            foreach (var name in part.Names)
            {
                if (part.Kind == "capability")
                {
                    Assert.True(OptionalCapabilityAction.IsSafeName(name), $"{part.Id}: {name}");
                    Assert.EndsWith("~~~", name, StringComparison.Ordinal);
                }
                else
                {
                    Assert.True(OptionalFeatureAction.IsSafeName(name), $"{part.Id}: {name}");
                }
            }
        }
    }

    [Fact]
    public void IdsAreUniqueAndNoNameBelongsToTwoPartsUnlessItIsADependency()
    {
        Assert.Equal(Parts.Count, Parts.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Text to speech names speech recognition too (it depends on it): the only name shared by two parts.
        var shared = Parts.SelectMany(p => p.Names.Select(n => (p.Id, Name: n))).GroupBy(x => x.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.All(shared, name => Assert.Equal("Language.Speech~~~", name));
    }

    [Fact]
    public void TextsFollowTheTextRules()
    {
        foreach (var text in Parts.SelectMany(p => new[] { p.Title, p.TitleDe, p.En, p.De }))
        {
            Assert.DoesNotContain('\u2013', text);
            Assert.DoesNotContain('\u2014', text);
            Assert.DoesNotContain('\u2022', text);
            Assert.DoesNotContain('\u00B7', text);
            Assert.DoesNotContain('\u2026', text);
            Assert.DoesNotContain("(s)", text);
        }
    }

    [Fact]
    public void TextToSpeechRemovesSpeechRecognitionFirstBecauseItDependsOnIt()
    {
        var names = Parts.Single(p => p.Id == "textToSpeech").Names.ToList();
        Assert.True(names.IndexOf("Language.Speech~~~") is var speech and >= 0 && speech < names.IndexOf("Language.TextToSpeech~~~"));
    }

    [Fact]
    public void ACapabilityPrefixMatchesEveryLanguageAndOnlyInstalledOnes()
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Language.Speech~~~en-US~0.0.1.0"] = "Installed", ["Language.Speech~~~de-DE~0.0.1.0"] = "Installed",
            ["Language.Speech~~~fr-FR~0.0.1.0"] = "Not Present", ["Language.TextToSpeech~~~en-US~0.0.1.0"] = "Installed",
        };
        var speech = new MediaPart("x", "capability", ["Language.Speech~~~"], "t", "t", "e", "d", "https://learn.microsoft.com/");
        Assert.Equal(["Language.Speech~~~de-DE~0.0.1.0", "Language.Speech~~~en-US~0.0.1.0"], MediaParts.Present(speech, states).Order());
    }

    [Fact]
    public void AFeatureMatchesItsExactNameWhenEnabled()
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Recall"] = "Enabled", ["RecallExtra"] = "Enabled", ["Other"] = "Disabled" };
        var recall = new MediaPart("x", "feature", ["Recall", "Other"], "t", "t", "e", "d", "https://learn.microsoft.com/");
        Assert.Equal(["Recall"], MediaParts.Present(recall, states));
    }
}
