using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Optimizer.App.Views;

/// <summary>
/// The rating of a tweak or check: its impact 0 to 5 as a light numeral and five bars, the same scale the website's rating
/// board uses. Filled bars are the rating; a disputed rating (the evidence does not hold up) draws them as outlines, so a
/// list can be read down this one column. Nothing is drawn for 0: most privacy and Explorer rows have no effect on the
/// profile's goal and stay quiet. The light weight is kept for what the scan says (this mark and the Overview's verdict).
/// </summary>
public sealed class RatingMark : StackPanel
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(int?), typeof(RatingMark), new PropertyMetadata(null, (d, _) => ((RatingMark)d).Update()));

    public static readonly DependencyProperty DisputedProperty =
        DependencyProperty.Register(nameof(Disputed), typeof(bool), typeof(RatingMark), new PropertyMetadata(false, (d, _) => ((RatingMark)d).Update()));

    public static readonly DependencyProperty KeepsPlaceProperty =
        DependencyProperty.Register(nameof(KeepsPlace), typeof(bool), typeof(RatingMark), new PropertyMetadata(true, (d, _) => ((RatingMark)d).Update()));

    /// <summary>Without a rating, keep the room (rows: the marks of a list stay in one column) or give it up (details pane).</summary>
    public bool KeepsPlace
    {
        get => (bool)GetValue(KeepsPlaceProperty);
        set => SetValue(KeepsPlaceProperty, value);
    }

    public static readonly DependencyProperty ShowsNumeralProperty =
        DependencyProperty.Register(nameof(ShowsNumeral), typeof(bool), typeof(RatingMark), new PropertyMetadata(true, (d, _) => ((RatingMark)d).Update()));

    /// <summary>The numeral before the bars: off where the words next to the mark already name the number (details pane).</summary>
    public bool ShowsNumeral
    {
        get => (bool)GetValue(ShowsNumeralProperty);
        set => SetValue(ShowsNumeralProperty, value);
    }

    /// <summary>Impact 0 to 5; null or 0 draws nothing.</summary>
    public int? Value
    {
        get => (int?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool Disputed
    {
        get => (bool)GetValue(DisputedProperty);
        set => SetValue(DisputedProperty, value);
    }

    private readonly TextBlock _numeral = new()
    {
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontWeight = FontWeights.Light,
        // One size in the rows and the details pane, so the mark reads the same everywhere.
        FontSize = 20,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
        MinWidth = 12,
        TextAlignment = TextAlignment.Right,
    };

    private readonly Border[] _bars = new Border[5];

    public RatingMark()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        // Screen readers get the words from the row (AutomationProperties.Name, "Impact 2 of 5"); the parts are drawing.
        _numeral.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Children.Add(_numeral);
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Border
            {
                Width = 5,
                Height = 12,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 0, i < _bars.Length - 1 ? 3 : 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Children.Add(_bars[i]);
        }
        Update();
    }

    private void Update()
    {
        var value = Math.Clamp(Value ?? 0, 0, 5);
        Visibility = value > 0 ? Visibility.Visible : KeepsPlace ? Visibility.Hidden : Visibility.Collapsed;
        _numeral.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _numeral.Visibility = ShowsNumeral ? Visibility.Visible : Visibility.Collapsed;
        _numeral.SetResourceReference(TextBlock.ForegroundProperty, Disputed ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];
            var on = i < value;
            // Theme brushes by key, so the mark follows a switch between light and dark.
            if (on && Disputed)
            {
                bar.Background = Brushes.Transparent;
                bar.BorderThickness = new Thickness(1);
                bar.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            }
            else
            {
                bar.BorderThickness = new Thickness(0);
                // The empty part of the scale is faint: the filled bars are what a list is read by.
                bar.SetResourceReference(Border.BackgroundProperty, on ? "AccentFillColorDefaultBrush" : "ControlAltFillColorQuarternaryBrush");
            }
        }
    }
}
