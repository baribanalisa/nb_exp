using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

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

        var input = rgba.Trim();

        // 1. Hex с # (#RGB, #RRGGBB, #AARRGGBB)
        if (input.StartsWith("#"))
        {
            if (TryParseHexColor(input, out var color))
                return CreateFrozenBrush(color);
        }

        // 2. rgb(r,g,b) или rgba(r,g,b,a)
        if (input.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseRgbFunction(input, out var color))
                return CreateFrozenBrush(color);
        }

        // 3. Hex без # (RGB, RRGGBB, AARRGGBB)
        if (IsHexString(input) && (input.Length == 3 || input.Length == 6 || input.Length == 8))
        {
            if (TryParseHexColor("#" + input, out var color))
                return CreateFrozenBrush(color);
        }

        // 4. Именованные цвета (Red, Blue, Green, etc.)
        try
        {
            var obj = new BrushConverter().ConvertFromString(input);
            if (obj is SolidColorBrush b)
            {
                if (!b.IsFrozen) b.Freeze();
                return b;
            }
        }
        catch { }

        // 5. Числовой формат: r g b [a] или r,g,b[,a]
        var parts = input.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
        return CreateFrozenBrush(col);
    }

    private static bool TryParseHexColor(string hex, out Color color)
    {
        color = default;
        try
        {
            var h = hex.TrimStart('#');

            // #RGB -> #RRGGBB
            if (h.Length == 3)
            {
                h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            }
            // #RGBA -> #AARRGGBB (web format RGBA to WPF ARGB)
            else if (h.Length == 4)
            {
                h = $"{h[3]}{h[3]}{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            }

            var obj = new BrushConverter().ConvertFromString("#" + h);
            if (obj is SolidColorBrush b)
            {
                color = b.Color;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryParseRgbFunction(string input, out Color color)
    {
        color = default;
        try
        {
            var lower = input.ToLowerInvariant();
            var start = lower.IndexOf('(');
            var end = lower.LastIndexOf(')');
            if (start < 0 || end < 0 || end <= start) return false;

            var inner = input.Substring(start + 1, end - start - 1);
            var parts = inner.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;

            double ParseComponent(string s)
            {
                s = s.Trim();
                if (s.EndsWith("%"))
                {
                    return double.Parse(s.TrimEnd('%'), CultureInfo.InvariantCulture) * 2.55;
                }
                return double.Parse(s, CultureInfo.InvariantCulture);
            }

            double ParseAlpha(string s)
            {
                s = s.Trim();
                if (s.EndsWith("%"))
                {
                    return double.Parse(s.TrimEnd('%'), CultureInfo.InvariantCulture) / 100.0 * 255.0;
                }
                var val = double.Parse(s, CultureInfo.InvariantCulture);
                return val <= 1.0 ? val * 255.0 : val;
            }

            var r = (byte)Math.Clamp(ParseComponent(parts[0]), 0, 255);
            var g = (byte)Math.Clamp(ParseComponent(parts[1]), 0, 255);
            var b = (byte)Math.Clamp(ParseComponent(parts[2]), 0, 255);
            byte a = 255;

            if (parts.Length >= 4)
            {
                a = (byte)Math.Clamp(ParseAlpha(parts[3]), 0, 255);
            }

            color = Color.FromArgb(a, r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHexString(string s)
    {
        foreach (char c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
