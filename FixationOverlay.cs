using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
// === ИСПРАВЛЕНИЕ КОНФЛИКТОВ ===
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using FlowDirection = System.Windows.FlowDirection; // Исправляет ошибку CS0176
using Pen = System.Windows.Media.Pen;     // <--- ДОБАВЛЕНО
using Point = System.Windows.Point;       // <--- ДОБАВЛЕНО
// ==============================
namespace NeuroBureau.Experiment;

public sealed record FixationSeries(IReadOnlyList<Fixation> Fixations, Color Color);

public sealed class FixationOverlay : FrameworkElement
{
    private IReadOnlyList<FixationSeries>? _series;
    private double? _currentTimeSec;
    private double _timeWindowSec;
    private bool _useTimeWindow;

    public double MinRadius { get; set; } = 14;
    public double MaxRadius { get; set; } = 46;
    public double MaxDurationForRadius { get; set; } = 1.2;
    public double Alpha { get; set; } = 0.75;
    public double LineWidth { get; set; } = 2.0;
    public FontFamily FontFamily { get; set; } = new("Segoe UI");
    public double FontSize { get; set; } = 16;

    public void ApplySettings(AnalysisVisualizationSettings settings)
    {
        if (settings == null) return;

        MinRadius = settings.MinRadius;
        MaxRadius = settings.MaxRadius;
        MaxDurationForRadius = settings.MaxDurationSec;
        LineWidth = settings.LineWidth;
        Alpha = settings.Alpha;
        FontFamily = new FontFamily(settings.FontFamily);
        FontSize = settings.FontSize;
        InvalidateVisual();
    }

    public void SetFixations(IReadOnlyList<Fixation>? fix)
    {
        if (fix == null || fix.Count == 0)
        {
            SetFixationSeries(null);
            return;
        }

        SetFixationSeries(new List<FixationSeries> { new(fix, DefaultColor) });
    }

    public void SetFixationSeries(IReadOnlyList<FixationSeries>? series)
    {
        _series = series;
        bool hasData = series != null && series.Any(s => s.Fixations.Count > 0);
        Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        InvalidateVisual();
    }

    public void SetPlaybackTime(double? currentTimeSec, double timeWindowSec, bool useTimeWindow)
    {
        _currentTimeSec = currentTimeSec;
        _timeWindowSec = timeWindowSec;
        _useTimeWindow = useTimeWindow;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_series == null || _series.Count == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var series in _series)
        {
            var visible = FilterVisible(series.Fixations, _currentTimeSec);
            if (visible.Count == 0) continue;

            var (strokeColor, fillColor) = BuildColors(series.Color);

            var pen = new Pen(new SolidColorBrush(strokeColor), LineWidth);
            pen.Freeze();

            var fill = new SolidColorBrush(fillColor);
            fill.Freeze();

            // линии
            for (int i = 1; i < visible.Count; i++)
            {
                var a = visible[i - 1];
                var b = visible[i];
                dc.DrawLine(pen, new Point(a.Xpx, a.Ypx), new Point(b.Xpx, b.Ypx));
            }

            // кружки + номера
            for (int i = 0; i < visible.Count; i++)
            {
                var f = visible[i];

                double k = MaxDurationForRadius > 0
                    ? Math.Clamp(f.DurSec / MaxDurationForRadius, 0, 1)
                    : 0;

                double r = MinRadius + (MaxRadius - MinRadius) * k;
                if (_currentTimeSec.HasValue && f.DurSec > 0)
                {
                    double end = f.StartSec + f.DurSec;
                    if (_currentTimeSec.Value >= f.StartSec && _currentTimeSec.Value <= end)
                    {
                        double progress = (_currentTimeSec.Value - f.StartSec) / f.DurSec;
                        progress = Math.Clamp(progress, 0, 1);
                        r = MinRadius + (MaxRadius - MinRadius) * k * progress;
                    }
                }

                var p = new Point(f.Xpx, f.Ypx);
                dc.DrawEllipse(fill, pen, p, r, r);

                var ft = new FormattedText(
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    FontSize,
                    Brushes.White,
                    dpi);

                dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
            }
        }
    }

    private static readonly Color DefaultColor = Color.FromRgb(0, 220, 255);

    private List<Fixation> FilterVisible(IReadOnlyList<Fixation> fixations, double? currentTime)
    {
        if (!currentTime.HasValue) return new List<Fixation>(fixations);

        double tMax = currentTime.Value;
        double tMin = _useTimeWindow ? tMax - _timeWindowSec : double.NegativeInfinity;

        var filtered = new List<Fixation>(fixations.Count);
        foreach (var f in fixations)
        {
            if (f.StartSec > tMax) break;
            if (_useTimeWindow && f.StartSec < tMin) continue;
            filtered.Add(f);
        }
        return filtered;
    }

    private (Color Stroke, Color Fill) BuildColors(Color baseColor)
    {
        double colorAlpha = baseColor.A / 255.0;
        byte strokeAlpha = (byte)Math.Round(Alpha * 255 * colorAlpha);
        byte fillAlpha = (byte)Math.Round(Alpha * 90 * colorAlpha);

        var stroke = Color.FromArgb(strokeAlpha, baseColor.R, baseColor.G, baseColor.B);
        var fill = Color.FromArgb(fillAlpha, baseColor.R, baseColor.G, baseColor.B);
        return (stroke, fill);
    }
}
