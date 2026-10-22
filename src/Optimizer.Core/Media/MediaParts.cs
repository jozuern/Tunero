namespace Optimizer.Core.Media;

/// <summary>
/// An optional part of Windows that the install media page can remove from install.wim (mediaparts.json): a capability
/// (Feature on Demand, removed with /Remove-Capability) or a feature (turned off with /Disable-Feature /Remove).
/// <paramref name="Names"/> are capability name prefixes up to the <c>~~~</c> (<c>Language.Speech~~~</c> matches the
/// speech data of every language) or exact feature names; they are removed in the order listed (a part that others depend
/// on is listed after them). <paramref name="Source"/> is the Microsoft page the texts rest on.
/// </summary>
public sealed record MediaPart(string Id, string Kind, IReadOnlyList<string> Names, string Title, string TitleDe, string En, string De, string Source)
{
    public const string Capability = "capability";
    public const string Feature = "feature";

    public string Label(string lang) => lang == "de" ? TitleDe : Title;

    public string Text(string lang) => lang == "de" ? De : En;
}

/// <summary>mediaparts.json: the parts in the order the page lists them and the build removes them.</summary>
public sealed class MediaPartCatalog
{
    public List<MediaPart> Parts { get; init; } = [];
}

public static class MediaParts
{
    /// <summary>The part with this id (any case), or null.</summary>
    public static MediaPart? Find(IEnumerable<MediaPart> parts, string id) =>
        parts.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The names of <paramref name="part"/> that the image holds, in removal order: capabilities in state Installed whose
    /// name starts with one of the part's prefixes, features in state Enabled named exactly like one of its names.
    /// <paramref name="states"/> are the image's names with their DISM state.
    /// </summary>
    public static IReadOnlyList<string> Present(MediaPart part, IReadOnlyDictionary<string, string> states)
    {
        var found = new List<string>();
        foreach (var name in part.Names)
        {
            if (part.Kind == MediaPart.Capability)
            {
                found.AddRange(states.Where(s => IsState(s.Value, "Installed") && s.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Key).Order(StringComparer.OrdinalIgnoreCase));
            }
            else if (states.TryGetValue(name, out var state) && IsState(state, "Enabled"))
            {
                found.Add(name);
            }
        }
        return found;
    }

    private static bool IsState(string state, string wanted) => state.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase);
}
