// File: MultiExportResultItem.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace NeuroBureau.Experiment;

public sealed class MultiExportResultItem : ObservableObject
{
    public string Uid { get; }
    public ResultFile Result { get; }

    public long DurationSec => Result.DurationSec;

    /// <summary>
    /// Отформатированная длительность (мм:сс или чч:мм:сс)
    /// </summary>
    public string DurationText
    {
        get
        {
            var ts = TimeSpan.FromSeconds(DurationSec);
            return ts.TotalHours >= 1 
                ? ts.ToString(@"hh\:mm\:ss") 
                : ts.ToString(@"mm\:ss");
        }
    }

    /// <summary>
    /// Дата и время эксперимента
    /// </summary>
    public DateTime Date
    {
        get
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(Result.TimeUnix).ToLocalTime().DateTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }

    public string DateText => Date != DateTime.MinValue 
        ? Date.ToString("dd.MM.yyyy HH:mm") 
        : "";

    public string StartTimeText
    {
        get
        {
            try
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(Result.TimeUnix).ToLocalTime().DateTime;
                return dt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>
    /// Имя испытуемого из chars-data
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Отображаемое имя (Имя или «Без имени» если имя отсутствует)
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Без имени" : Name;

    /// <summary>
    /// Полная строка для отображения: Имя | Дата | Длительность
    /// </summary>
    public string FullDisplayText
    {
        get
        {
            var parts = new List<string>();
            
            if (!string.IsNullOrWhiteSpace(Name))
                parts.Add(Name);
            else
                parts.Add(Uid);
                
            if (!string.IsNullOrWhiteSpace(DateText))
                parts.Add(DateText);
                
            parts.Add(DurationText);
            
            return string.Join(" • ", parts);
        }
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public MultiExportResultItem(string uid, ResultFile result, bool isSelected)
    {
        Uid = uid;
        Result = result;
        _isSelected = isSelected;
        Name = GetResultName(result.CharsData);
    }

    private static string GetResultName(List<CharValue>? charsData)
    {
        if (charsData == null || charsData.Count == 0) return "";

        // Ищем характеристику с именем
        var nameChar = charsData.FirstOrDefault(c =>
            c.Name != null && (
                c.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("имя", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("participant", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("участник", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("испытуемый", StringComparison.OrdinalIgnoreCase)));

        return nameChar?.Val ?? "";
    }
}
