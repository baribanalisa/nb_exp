using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
namespace NeuroBureau.Experiment;

public sealed class RgbaToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        var brush = TryParseRgbaBrush(text);
        return brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() ?? "";

    private static Brush? TryParseRgbaBrush(string? rgba)
    {
        if (string.IsNullOrWhiteSpace(rgba)) return null;

        // "#RRGGBB" / "#AARRGGBB"
        if (rgba.TrimStart().StartsWith("#"))
        {
            try
            {
                var obj = new BrushConverter().ConvertFromString(rgba);
                if (obj is SolidColorBrush b)
                {
                    if (!b.IsFrozen) b.Freeze();
                    return b;
                }
            }
            catch { }
        }

        // "1 0 0 1" or "255,0,0,255"
        var parts = rgba.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;

        if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var r)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var g)) return null;
        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var b2)) return null;

        double a = 1.0;
        if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var aa))
            a = aa;

        bool unit01 = (r <= 1 && g <= 1 && b2 <= 1 && a <= 1);

        byte ToByte(double v)
        {
            if (unit01) v *= 255.0;
            v = Math.Clamp(v, 0, 255);
            return (byte)Math.Round(v);
        }

        var col = Color.FromArgb(ToByte(a), ToByte(r), ToByte(g), ToByte(b2));
        var br = new SolidColorBrush(col);
        br.Freeze();
        return br;
    }
}