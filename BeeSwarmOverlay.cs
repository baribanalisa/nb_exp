using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

// Псевдонимы
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace NeuroBureau.Experiment;

// Структура для хранения готовых координат (уже переведенных в пиксели экрана/стимула)
public readonly record struct BeeSample(double Time, float Xpx, float Ypx);

public sealed record BeeSeries(IReadOnlyList<BeeSample> Samples, Color Color);

public sealed class BeeSwarmOverlay : FrameworkElement
{
    private IReadOnlyList<BeeSeries>? _series;
    private double _currentTime;
    
    // Локальные копии настроек для быстрого доступа при отрисовке
    private double _radius = 20;
    private double _lineWidth = 3;
    private double _alpha = 0.8;

    public void ApplySettings(AnalysisVisualizationSettings settings)
    {
        if (settings == null) return;
        _radius = settings.BeeRadius;
        _lineWidth = settings.BeeLineWidth;
        // Можно использовать общий Alpha или сделать отдельный BeeAlpha, пока возьмем общий
        _alpha = settings.Alpha; 
        InvalidateVisual();
    }

    public void SetSeries(IReadOnlyList<BeeSeries>? series)
    {
        _series = series;
        InvalidateVisual();
    }

    public void SetTime(double time)
    {
        _currentTime = time;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_series == null || _series.Count == 0) return;

        foreach (var ser in _series)
        {
            if (ser.Samples.Count == 0) continue;

            // Ищем сэмпл, ближайший к текущему времени
            // Т.к. данные упорядочены по времени, используем бинарный поиск
            var sample = FindNearestSample(ser.Samples, _currentTime);
            
            // Если сэмпл найден и он не слишком далеко по времени (например, в пределах 100мс)
            // (Если разрыв в записи, пчела должна исчезнуть)
            if (sample.HasValue && Math.Abs(sample.Value.Time - _currentTime) < 0.1) 
            {
                DrawBee(dc, sample.Value, ser.Color);
            }
        }
    }

    private void DrawBee(DrawingContext dc, BeeSample s, Color color)
    {
        // Цвет с учетом прозрачности
        var drawColor = Color.FromArgb((byte)(255 * _alpha), color.R, color.G, color.B);
        
        var pen = new Pen(new SolidColorBrush(drawColor), _lineWidth);
        pen.Freeze();

        // Рисуем только контур (stroke), как в описании
        dc.DrawEllipse(null, pen, new Point(s.Xpx, s.Ypx), _radius, _radius);
    }

    private BeeSample? FindNearestSample(IReadOnlyList<BeeSample> list, double targetTime)
    {
        int left = 0;
        int right = list.Count - 1;
        
        if (right < 0) return null;

        // Если время за пределами диапазона
        if (targetTime <= list[0].Time) return list[0];
        if (targetTime >= list[right].Time) return list[right];

        // Бинарный поиск
        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            if (list[mid].Time < targetTime)
                left = mid + 1;
            else
                right = mid - 1;
        }

        // left указывает на первый элемент, который >= targetTime
        // Проверяем, кто ближе: left или left-1
        BeeSample s1 = list[left - 1]; // элемент, который меньше targetTime
        BeeSample s2 = list[left];     // элемент, который больше или равен targetTime

        return (targetTime - s1.Time < s2.Time - targetTime) ? s1 : s2;
    }
}