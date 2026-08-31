using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GeroImperium.App.Views.Controls;

/// <summary>
/// Hand-rolled preset-swatches + hex-entry background color picker -- WPF-UI ships no ColorPicker (the open
/// item flagged in pc_app_plan.md). SelectedColor is nullable: null means "no background override," matching
/// Application/GeroImperiumKey.BackgroundColorArgb's meaning (the device only stores a flat image; background
/// compositing is an authoring-time step, see pc_app_plan.md "Data model decisions").
/// </summary>
public partial class ColorSwatchPicker : UserControl
{
    private static readonly Color[] Palette =
    [
        Colors.Red, Colors.OrangeRed, Colors.Orange, Colors.Gold, Colors.Yellow, Colors.YellowGreen,
        Colors.Green, Colors.Teal, Colors.Cyan, Colors.DodgerBlue, Colors.Blue, Colors.Indigo,
        Colors.Purple, Colors.Magenta, Colors.HotPink, Colors.SaddleBrown,
        Colors.Gray, Colors.Black, Colors.White,
    ];

    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor),
        typeof(Color?),
        typeof(ColorSwatchPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    public Color? SelectedColor
    {
        get => (Color?)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public ColorSwatchPicker()
    {
        InitializeComponent();

        foreach (var color in Palette)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(color),
                Cursor = Cursors.Hand,
                Tag = color,
            };
            swatch.MouseLeftButtonUp += Swatch_MouseLeftButtonUp;
            SwatchPanel.Children.Add(swatch);
        }

        UpdatePreview();
    }

    private void Swatch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SelectedColor = (Color)((Border)sender).Tag;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = null;
    }

    private void HexTextBox_LostFocus(object sender, RoutedEventArgs e) => TryApplyHexText();

    private void HexTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryApplyHexText();
        }
    }

    private void TryApplyHexText()
    {
        var text = HexTextBox.Text.Trim();
        if (text.Length == 0)
        {
            SelectedColor = null;
            return;
        }

        if (TryParseHexColor(text, out var color))
        {
            SelectedColor = color;
        }
        else
        {
            UpdatePreview(); // reject the edit, restore the last valid text/preview
        }
    }

    private static bool TryParseHexColor(string text, out Color color)
    {
        color = default;
        var hex = text.StartsWith('#') ? text[1..] : text;

        if (hex.Length == 6 && byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            color = Color.FromRgb(r, g, b);
            return true;
        }

        if (hex.Length == 8 && byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a)
            && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r2)
            && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g2)
            && byte.TryParse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b2))
        {
            color = Color.FromArgb(a, r2, g2, b2);
            return true;
        }

        return false;
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ColorSwatchPicker)d).UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (SelectedColor is Color color)
        {
            PreviewBrushElement.Color = color;
            HexTextBox.Text = color.A == 255
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        else
        {
            PreviewBrushElement.Color = Colors.Transparent;
            HexTextBox.Text = string.Empty;
        }
    }
}
