namespace Optimizer.Core;

/// <summary>A catalog entry with an English and a German text (service notes, features, apps, removable apps).</summary>
public interface IBilingual
{
    string En { get; }
    string De { get; }
}

/// <summary>The one rule for catalog texts in two languages: German when the app runs in German and there is one, else English.</summary>
public static class Bilingual
{
    public static string Pick(string lang, string en, string de) => lang == "de" && de.Length > 0 ? de : en;

    public static string Text(this IBilingual entry, string lang) => Pick(lang, entry.En, entry.De);
}
