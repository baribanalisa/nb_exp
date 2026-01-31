
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

// === РЕШЕНИЕ КОНФЛИКТОВ ===
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
// ==========================
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NeuroBureau.Experiment;

public enum AoiType
{
    Rectangle,
    Ellipse,
    Polygon,   // FreeForm
    Polyline   // StraightLines
}

public class AoiElement
{
    public string Uid { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "AOI";
    public AoiType Type { get; set; }
    
    public List<Point> NormalizedPoints { get; set; } = new();

    public string ColorHex { get; set; } = "#FFFF0000";
    public double LineWidth { get; set; } = 2.0;
    public double FontSize { get; set; } = 12.0;
    
    public bool ShowCallout { get; set; } = false;
    public double CalloutOffsetX { get; set; } = 20;
    public double CalloutOffsetY { get; set; } = -20;

    [JsonIgnore]
    public Color Color
    {
        get
        {
            try 
            { 
                // Явно указываем System.Windows.Media
                return (Color)System.Windows.Media.ColorConverter.ConvertFromString(ColorHex); 
            }
            catch { return Colors.Red; }
        }
    }
}

public class AoiMetricsResult
{
    public string AoiName { get; set; } = "";
    
    // Существовавшие
    public int FixationCount { get; set; }
    public double TotalDwellTime { get; set; }
    public double TimeToFirstFixation { get; set; } = 0;
    public int RevisitCount { get; set; }
    public double AverageFixationDuration { get; set; }

    // НОВЫЕ ПОКАЗАТЕЛИ (из вашего списка)
    public int FixationsBeforeFirst { get; set; }       // Кол-во фиксаций до первой фиксации
    public double TimeBeforeFirstFixation { get; set; } // Время до первой фиксации (обычно = TTFF, но добавим явно)
    public double FirstFixationDuration { get; set; }   // Продолжительность первой фиксации
    public int SaccadeCount { get; set; }               // Количество саккад (входящих/внутренних)
    public double AverageSaccadeAmplitude { get; set; } // Средняя амплитуда саккад (в px)
    public double AverageSaccadeAmplitudeDeg { get; set; } // Средняя амплитуда саккад (в градусах)
    public double ScanpathLength { get; set; }          // Общая длина пути (px)
    public double ScanpathLengthDeg { get; set; }       // Общая длина пути (в градусах)
    public double AreaRatio { get; set; }               // % площади (AOI / Стимул)
}

// Обновляем обертку для таблицы
public class AoiDisplayItem : System.ComponentModel.INotifyPropertyChanged
{
    private readonly AoiElement _aoi;
    private readonly AoiMetricsResult? _metrics;

    public AoiDisplayItem(AoiElement aoi, AoiMetricsResult? metrics)
    {
        _aoi = aoi;
        _metrics = metrics;
    }

    public string Name
    {
        get => _aoi.Name;
        set { _aoi.Name = value; OnPropertyChanged(); }
    }

    public string ColorHex => _aoi.ColorHex;
    public AoiElement SourceAoi => _aoi;

    // Свойства для колонок
    public int FixationCount => _metrics?.FixationCount ?? 0;
    public double TotalDwellTime => _metrics?.TotalDwellTime ?? 0;
    public double TimeToFirstFixation => _metrics?.TimeToFirstFixation ?? 0;
    public int RevisitCount => _metrics?.RevisitCount ?? 0;
    public double AvgFixationDuration => _metrics?.AverageFixationDuration ?? 0;
    
    // Новые
    public int FixationsBeforeFirst => _metrics?.FixationsBeforeFirst ?? 0;
    public double FirstFixationDuration => _metrics?.FirstFixationDuration ?? 0;
    public int SaccadeCount => _metrics?.SaccadeCount ?? 0;
    public double AvgSaccadeAmplitude => _metrics?.AverageSaccadeAmplitude ?? 0;
    public double AvgSaccadeAmplitudeDeg => _metrics?.AverageSaccadeAmplitudeDeg ?? 0;
    public double ScanpathLength => _metrics?.ScanpathLength ?? 0;
    public double ScanpathLengthDeg => _metrics?.ScanpathLengthDeg ?? 0;
    public double AreaRatio => (_metrics?.AreaRatio ?? 0) * 100.0; // В процентах

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? prop = null) 
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
}
