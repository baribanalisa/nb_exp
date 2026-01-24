// File: StimulusDisplayNameConverter.cs
using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace NeuroBureau.Experiment;

public sealed class StimulusDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        s = s.Trim();

        // Ожидаемый формат: "<uid> — <name>"
        // Если разделителя нет — просто показываем то, что пришло.
        var namePart = ExtractAfterDash(s);
        if (string.IsNullOrWhiteSpace(namePart))
            namePart = s;

        // На всякий случай: если вдруг там путь — оставим только имя файла
        try
        {
            var fileName = Path.GetFileName(namePart.Trim());
            return string.IsNullOrWhiteSpace(fileName) ? namePart.Trim() : fileName;
        }
        catch
        {
            return namePart.Trim();
        }
    }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;


    private static string ExtractAfterDash(string s)
    {
        // Основной кейс в UI — длинное тире.
        var idx = s.IndexOf('—');
        if (idx >= 0)
            return s[(idx + 1)..].Trim();

        // На всякий случай: короткое тире.
        idx = s.IndexOf('–');
        if (idx >= 0)
            return s[(idx + 1)..].Trim();

        return s;
    }
}
