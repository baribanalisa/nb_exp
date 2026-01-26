using System;
using System.Globalization;
using System.Windows.Data;

namespace NeuroBureau.Experiment;

public sealed class MultiExportModeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MultiExportMode mode)
        {
            return mode switch
            {
                MultiExportMode.SeparateFiles => "Отдельные файлы",
                MultiExportMode.FilePerStimul => "Файл на стимул",
                MultiExportMode.FilePerResult => "Файл на результат",
                MultiExportMode.AllInOne => "Все в одном",
                _ => mode.ToString()
            };
        }

        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}