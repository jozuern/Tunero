using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace Optimizer.App.Services;

/// <summary>
/// Binding-based localizer: XAML binds to <c>Loc.Instance[key]</c>, so switching the language
/// updates the UI live (x:Static would not).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("Optimizer.App.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>
    /// The culture of Windows' regional settings, read before <see cref="SetLanguage"/> replaces the current culture with
    /// the app language: files other programs open (a CSV for Excel) follow it, not the app language.
    /// </summary>
    public static CultureInfo Regional { get; } = CultureInfo.CurrentCulture;

    public static Loc Instance { get; } = new();

    private CultureInfo _culture = Pick(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    /// <summary>"en" or "de": also the language of the explanation pages.</summary>
    public string Language => _culture.TwoLetterISOLanguageName;

    public string this[string key] => Resources.GetString(key, _culture) ?? key;

    public string Format(string key, params object?[] args) => string.Format(_culture, this[key], args);

    public void SetLanguage(string language)
    {
        var culture = Pick(language);
        // Numbers and dates follow the app language too (also when it matches the start language), so an English
        // page on a PC with German regional settings does not show "33,8 GB".
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (culture.Name == _culture.Name) return;
        _culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static CultureInfo Pick(string language) => language == "de" ? new CultureInfo("de") : new CultureInfo("en");
}

/// <summary>{l:Tr Key} -> binding to Loc.Instance[Key].</summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
