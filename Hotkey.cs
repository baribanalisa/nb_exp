using System;
using System.Windows.Input;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
namespace NeuroBureau.Experiment;

public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key);

public static class HotkeyUtil
{
    public static bool IsModifierKey(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
          or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public static bool Matches(KeyEventArgs e, Hotkey hk)
    {
        var key = (e.Key == Key.System) ? e.SystemKey : e.Key;

        // Enter vs Return (в WPF иногда прилетает Return)
        if ((key == Key.Return && hk.Key == Key.Enter) || (key == Key.Enter && hk.Key == Key.Return))
            return Keyboard.Modifiers == hk.Modifiers;

        return key == hk.Key && Keyboard.Modifiers == hk.Modifiers;
    }

    public static string Format(Hotkey hk)
    {
        string s = "";
        if (hk.Modifiers.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (hk.Modifiers.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        if (hk.Modifiers.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        if (hk.Modifiers.HasFlag(ModifierKeys.Windows)) s += "Win+";

        var key = hk.Key switch
        {
            Key.Return => "Enter",
            Key.Space => "Space",
            Key.Escape => "Escape",
            _ => hk.Key.ToString()
        };
        return s + key;
    }

    public static string SerializeModifiers(ModifierKeys mods)
    {
        if (mods == ModifierKeys.None) return "None";
        string s = "";
        if (mods.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        if (mods.HasFlag(ModifierKeys.Windows)) s += "Win+";
        return s.TrimEnd('+');
    }

    public static string SerializeKey(Key key) =>
        key switch
        {
            Key.Return => "Enter",
            _ => key.ToString()
        };

    public static Hotkey Parse(string? modifierStr, string? keyStr, Hotkey fallback)
    {
        var mods = ParseModifiers(modifierStr) ?? fallback.Modifiers;
        var key = ParseKey(keyStr) ?? fallback.Key;
        return new Hotkey(mods, key);
    }

    private static ModifierKeys? ParseModifiers(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Equals("None", StringComparison.OrdinalIgnoreCase)) return ModifierKeys.None;

        ModifierKeys mods = ModifierKeys.None;
        var parts = s.Split(new[] { '+', ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p0 in parts)
        {
            var p = p0.Trim();
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Control;
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Alt;
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Shift;
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) || p.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Windows;
        }
        return mods;
    }

    private static Key? ParseKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();

        if (s.Equals("Space", StringComparison.OrdinalIgnoreCase)) return Key.Space;
        if (s.Equals("Esc", StringComparison.OrdinalIgnoreCase) || s.Equals("Escape", StringComparison.OrdinalIgnoreCase)) return Key.Escape;
        if (s.Equals("Enter", StringComparison.OrdinalIgnoreCase) || s.Equals("Return", StringComparison.OrdinalIgnoreCase)) return Key.Enter;

        if (Enum.TryParse<Key>(s, true, out var k)) return k;
        return null;
    }
}
