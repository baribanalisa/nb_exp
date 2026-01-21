using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
namespace NeuroBureau.Experiment;

public sealed class SplitDevicesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s))
            return Array.Empty<string>();

        // DevicesText вида: "Айтрекер + КГР + Клавиатура/мышь"
        return s.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct()
                .ToList();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
