using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Optimizer.App.Services;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.Views;

/// <summary>
/// Small question dialog. The last button is the primary (accent) one; the result is the clicked index, or -1. Keep the
/// body to one or two sentences: what cannot be taken back goes into <see cref="WithWarning"/>, side notes into
/// <see cref="WithDetails"/>.
/// </summary>
public partial class MessageWindow : FluentWindow
{
    // Footer width inside the 24 px padding, and the space a button needs around its label (padding, border, gap).
    private const double FooterWidth = 560 - 2 * 24;
    private const double ButtonChrome = 2 * 12 + 2 + 8;

    public MessageWindow(string title, string body, params string[] buttons)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        BodyText.Text = body;
        // The order of a Windows 11 content dialog: the primary (accent) button first, then the secondary one, Cancel last.
        // The callers list them the other way round (Cancel first, primary last). The buttons share the footer in equal
        // columns when every label fits on one line that way; otherwise each takes the width of its label, right aligned,
        // so no label wraps. Only labels too long even for that share equal columns and wrap.
        var widths = buttons.Select(b => LabelWidth(b) + ButtonChrome).ToList();
        var equal = buttons.Length * widths.Max() <= FooterWidth || widths.Sum() > FooterWidth;
        var wrap = widths.Sum() > FooterWidth;
        if (!equal) Buttons.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            Buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = equal ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var primary = i == buttons.Length - 1;
            var column = buttons.Length - 1 - i;
            var b = new Wpf.Ui.Controls.Button
            {
                Content = wrap ? new System.Windows.Controls.TextBlock { Text = buttons[i], TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center } : buttons[i],
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                MinWidth = 96,
                Margin = new Thickness(column == 0 ? 0 : 4, 0, column == buttons.Length - 1 ? 0 : 4, 0),
                Appearance = primary ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary,
            };
            Grid.SetColumn(b, column);
            b.Click += (_, _) =>
            {
                Result = index;
                DialogResult = true;
            };
            Buttons.Children.Add(b);
        }
        // Escape closes the dialog like the window's close button: no choice made (-1).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            DialogResult = false;
        };
    }

    public int Result { get; private set; } = -1;

    /// <summary>A warning bar under the text, for what cannot be taken back.</summary>
    public MessageWindow WithWarning(string title, string message)
    {
        WarningBar.Title = title;
        WarningBar.Message = message;
        WarningBar.IsOpen = true;
        return this;
    }

    /// <summary>Side notes in small secondary text under the rest, such as what stays on the PC.</summary>
    public MessageWindow WithDetails(string text)
    {
        DetailsText.Text = text;
        DetailsText.Visibility = Visibility.Visible;
        return this;
    }

    // Width of a button label in the body font, to decide whether the buttons fit in equal columns.
    private double LabelWidth(string text) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            14, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    public static int Show(Window? owner, string title, string body, params string[] buttons) =>
        Show(owner, new MessageWindow(title, body, buttons));

    public static int Show(Window? owner, MessageWindow w)
    {
        w.Owner = owner;
        return w.ShowDialog() == true ? w.Result : -1;
    }
}

/// <summary>IDialogs implementation for the view model.</summary>
/// <param name="documentsFolder">Where the file dialogs open: the signed-in user's Documents (null: this process's own).</param>
public sealed class Dialogs(Func<string?>? documentsFolder = null) : IDialogs
{
    private static Window? Owner => Application.Current.MainWindow;

    private string StartFolder => documentsFolder?.Invoke() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public ConfirmResult? ConfirmApply(ConfirmRequest request)
    {
        var w = new ConfirmWindow(request) { Owner = Owner };
        return w.ShowDialog() == true ? new ConfirmResult(w.Acknowledged) : null;
    }

    public RestorePointChoice AskRestorePoint() =>
        MessageWindow.Show(Owner, Loc.Instance["Rp_AskTitle"], Loc.Instance["Rp_AskText"],
                Loc.Instance["Confirm_Cancel"], Loc.Instance["Rp_Continue"], Loc.Instance["Rp_Enable"]) switch
        {
            2 => RestorePointChoice.Enable,
            1 => RestorePointChoice.Continue,
            _ => RestorePointChoice.Cancel,
        };

    public bool ConfirmExpertMode() => MessageWindow.Show(Owner, ExpertDialog()) == 1;

    /// <summary>The Expert mode question (also rendered by the --message-shot developer switch).</summary>
    public static MessageWindow ExpertDialog() =>
        new MessageWindow(Loc.Instance["Expert_ConfirmTitle"], Loc.Instance["Expert_ConfirmText"], Loc.Instance["Confirm_Cancel"], Loc.Instance["Expert_ConfirmButton"])
            .WithWarning(Loc.Instance["Expert_ConfirmWarningTitle"], Loc.Instance["Expert_ConfirmWarning"]);

    public bool Ask(string title, string text, string primary) =>
        MessageWindow.Show(Owner, title, text, Loc.Instance["Confirm_Cancel"], primary) == 1;

    public string? PickSaveFile(string defaultName, string filterName, string extension)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = defaultName,
            Filter = $"{filterName} (*{extension})|*{extension}",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = StartFolder,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string filterName, string extension)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"{filterName} (*{extension})|*{extension}",
            CheckFileExists = true,
            InitialDirectory = StartFolder,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool ConfirmUndoAll(int count) =>
        MessageWindow.Show(Owner, Loc.Instance["UndoAll_Title"], Loc.Instance.Format("UndoAll_Text", count),
            Loc.Instance["Confirm_Cancel"], Loc.Instance["Changes_UndoAll"]) == 1;

    public UninstallChoice ConfirmUninstall(int activeChanges, string dataFolder)
    {
        var dialog = UninstallDialog(activeChanges, dataFolder);
        if (activeChanges == 0)
            return MessageWindow.Show(Owner, dialog) == 1 ? UninstallChoice.Keep : UninstallChoice.Cancel;
        return MessageWindow.Show(Owner, dialog) switch
        {
            2 => UninstallChoice.UndoFirst,
            1 => UninstallChoice.Keep,
            _ => UninstallChoice.Cancel,
        };
    }

    /// <summary>
    /// The uninstall question: what goes in one sentence, active changes as a warning (their backups go with the data
    /// folder), what stays as details. Buttons: Cancel, Uninstall, and with active changes Undo and uninstall (primary).
    /// Also rendered by the --message-shot developer switch.
    /// </summary>
    public static MessageWindow UninstallDialog(int activeChanges, string dataFolder)
    {
        // The folder on a line of its own, so its path does not break in the middle of the sentence.
        var details = Loc.Instance["Uninstall_Details"] + "\n" + Loc.Instance.Format("Uninstall_DataFolder", dataFolder);
        if (activeChanges == 0)
            return new MessageWindow(Loc.Instance["Uninstall_Title"], Loc.Instance["Uninstall_Text"], Loc.Instance["Confirm_Cancel"], Loc.Instance["Uninstall_Button"])
                .WithDetails(details);
        return new MessageWindow(Loc.Instance["Uninstall_Title"], Loc.Instance["Uninstall_Text"],
                Loc.Instance["Confirm_Cancel"], Loc.Instance["Uninstall_Keep"], Loc.Instance["Uninstall_UndoFirst"])
            .WithWarning(Loc.Instance.Format("Uninstall_WarningTitle", activeChanges), Loc.Instance["Uninstall_Warning"])
            .WithDetails(details);
    }
}
