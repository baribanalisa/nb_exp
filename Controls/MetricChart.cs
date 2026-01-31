using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
// === ИСПРАВЛЕНИЕ КОНФЛИКТОВ ===
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;           // <--- ДОБАВЛЕНО
using FlowDirection = System.Windows.FlowDirection; // Важно!
using Application = System.Windows.Application;     // Важно!
// ==============================
namespace NeuroBureau.Experiment.Controls;

public readonly record struct MetricPoint(double TimeSec, double Value);
public readonly record struct MetricSeries(IReadOnlyList<MetricPoint> Points, Color Color);

public sealed class MetricChart : FrameworkElement
{
    private IReadOnlyList<MetricSeries>? _series;
    private IReadOnlyList<(double StartSec, double EndSec)>? _fixBands;

    private double _tMin, _tMax;
    private double _yMin, _yMax;
    private double _threshold;
    private string _title = "";
    private string _yUnit = "";
    private string _emptyText = "—";

    private string _xUnit = "с";

    private double _currentTime = double.NaN;
    private static readonly Brush CurrentTimeLine = new SolidColorBrush(Color.FromArgb(0xCC, 0xE5, 0x39, 0x35));

    private const double Pad = 10;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush Border = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));
    private static readonly Brush Grid = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0));
    private static readonly Brush Text = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0));
    private static readonly Brush Line = new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0x22, 0x22));
    private static readonly Brush Thr = new SolidColorBrush(Color.FromArgb(0xFF, 0xCC, 0x33, 0x33));
    private static readonly Brush FixBand = new SolidColorBrush(Color.FromArgb(0x22, 0x33, 0x66, 0xCC));

    static MetricChart()
    {
        (Bg as SolidColorBrush)?.Freeze();
        (Border as SolidColorBrush)?.Freeze();
        (Grid as SolidColorBrush)?.Freeze();
        (Text as SolidColorBrush)?.Freeze();
        (Line as SolidColorBrush)?.Freeze();
        (Thr as SolidColorBrush)?.Freeze();
        (FixBand as SolidColorBrush)?.Freeze();
        (CurrentTimeLine as SolidColorBrush)?.Freeze();
    }

    public void Clear(string? text = null)
    {
        _series = null;
        _fixBands = null;
        _currentTime = double.NaN;
        _emptyText = string.IsNullOrWhiteSpace(text) ? "—" : text;
        InvalidateVisual();
    }

    public void SetCurrentTime(double time)
    {
        if (Math.Abs(_currentTime - time) < 0.001) return;
        _currentTime = time;
        InvalidateVisual();
    }

    public void ClearCurrentTime()
    {
        if (double.IsNaN(_currentTime)) return;
        _currentTime = double.NaN;
        InvalidateVisual();
    }

    public void SetData(
        IReadOnlyList<MetricSeries> series,
        double tMin, double tMax,
        double yMin, double yMax,
        double threshold,
        string title,
        IReadOnlyList<(double StartSec, double EndSec)>? fixBands = null,
        string? yUnit = null,
        string? xUnit = null)
    {
        _series = series;
        _fixBands = fixBands;

        _tMin = tMin;
        _tMax = Math.Max(tMin + 1e-6, tMax);

        _yMin = yMin;
        _yMax = Math.Max(yMin + 1e-6, yMax);

        _threshold = threshold;
        _title = title ?? "";
        _yUnit = yUnit ?? "";
        _xUnit = xUnit ?? "с";

        _emptyText = "—";

        InvalidateVisual();
    }

    public void SetData(
        IReadOnlyList<MetricPoint> points,
        double tMin, double tMax,
        double yMin, double yMax,
        double threshold,
        string title,
        IReadOnlyList<(double StartSec, double EndSec)>? fixBands = null,
        string? yUnit = null,
        string? xUnit = null)
    {
        SetData(new List<MetricSeries> { new(points, ((SolidColorBrush)Line).Color) },
            tMin, tMax, yMin, yMax, threshold, title, fixBands, yUnit, xUnit);
    }

    private Rect GetPlotRect()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2) return new Rect(0, 0, 0, 0);

        return new Rect(Pad, 24, Math.Max(1, w - 2 * Pad), Math.Max(1, h - 24 - Pad));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2)
            return;

        var rect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Bg, new Pen(Border, 1), rect, 10, 10);

        // title
        DrawText(dc, _title, 12, new Point(Pad, 4));

        var plot = new Rect(Pad, 24, Math.Max(1, w - 2 * Pad), Math.Max(1, h - 24 - Pad));

        // grid
        for (int i = 1; i <= 4; i++)
        {
            double x = plot.Left + plot.Width * i / 5.0;
            dc.DrawLine(new Pen(Grid, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
        for (int i = 1; i <= 2; i++)
        {
            double y = plot.Top + plot.Height * i / 3.0;
            dc.DrawLine(new Pen(Grid, 1), new Point(plot.Left, y), new Point(plot.Right, y));
        }

        // empty?
        if (_series == null || _series.Count == 0 || _series.All(s => s.Points.Count < 2))
        {
            DrawText(dc, _emptyText, 12, new Point(Pad, plot.Top + plot.Height / 2 - 8));
            return;
        }

        // fixation bands (background)
        if (_fixBands != null && _fixBands.Count > 0)
        {
            foreach (var (s, e) in _fixBands)
            {
                var x1 = X(s, plot);
                var x2 = X(e, plot);
                if (x2 < plot.Left || x1 > plot.Right) continue;

                var rr = new Rect(
                    Math.Max(plot.Left, Math.Min(plot.Right, x1)),
                    plot.Bottom - 10,
                    Math.Max(1, Math.Min(plot.Right, x2) - Math.Max(plot.Left, x1)),
                    8);

                dc.DrawRoundedRectangle(FixBand, null, rr, 2, 2);
            }
        }

        // threshold line
        if (!double.IsNaN(_threshold) && !double.IsInfinity(_threshold))
        {
            var yThr = Y(_threshold, plot);
            dc.DrawLine(new Pen(Thr, 1.5), new Point(plot.Left, yThr), new Point(plot.Right, yThr));
        }

        // polyline per series
        foreach (var series in _series)
        {
            if (series.Points.Count < 2) continue;

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool started = false;
                foreach (var p in series.Points)
                {
                    if (p.TimeSec < _tMin || p.TimeSec > _tMax) continue;
                    if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

                    var pt = new Point(X(p.TimeSec, plot), Y(p.Value, plot));
                    if (!started)
                    {
                        g.BeginFigure(pt, false, false);
                        started = true;
                    }
                    else
                    {
                        g.LineTo(pt, true, false);
                    }
                }
            }
            geo.Freeze();

            var brush = new SolidColorBrush(series.Color);
            brush.Freeze();
            dc.DrawGeometry(null, new Pen(brush, 1.5), geo);
        }

        // current time marker
        if (!double.IsNaN(_currentTime) && _currentTime >= _tMin && _currentTime <= _tMax)
        {
            var xCur = X(_currentTime, plot);
            dc.DrawLine(new Pen(CurrentTimeLine, 2), new Point(xCur, plot.Top), new Point(xCur, plot.Bottom));
        }

        // y labels (min/max) с единицами измерения
        var yUnitSuffix = string.IsNullOrWhiteSpace(_yUnit) ? "" : $" {_yUnit}";
        DrawTextRight(dc, $"{_yMax:0}{yUnitSuffix}", 11, new Point(plot.Right, plot.Top - 2));
        DrawTextRight(dc, $"{_yMin:0}{yUnitSuffix}", 11, new Point(plot.Right, plot.Bottom - 14));

        // x labels (min/max) с единицами измерения
        var xUnitSuffix = string.IsNullOrWhiteSpace(_xUnit) ? "" : $" {_xUnit}";
        DrawText(dc, $"{_tMin:0.0}{xUnitSuffix}", 10, new Point(plot.Left, plot.Bottom + 2));
        DrawTextRight(dc, $"{_tMax:0.0}{xUnitSuffix}", 10, new Point(plot.Right, plot.Bottom + 2));
    }

    private double X(double t, Rect plot)
    {
        var d = _tMax - _tMin;
        if (d <= 1e-9) d = 1e-9;
        return plot.Left + (t - _tMin) / d * plot.Width;
    }

    private double Y(double v, Rect plot)
    {
        var d = _yMax - _yMin;
        if (d <= 1e-9) d = 1e-9;
        return plot.Bottom - (v - _yMin) / d * plot.Height;
    }

    private static FormattedText CreateText(string s, double size)
    {
        var dpi = Application.Current?.MainWindow != null
            ? VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip
            : 1.0;

        return new FormattedText(
            s,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            Text,
            dpi);
    }

    private static void DrawText(DrawingContext dc, string s, double size, Point at)
    {
        if (string.IsNullOrEmpty(s)) return;
        dc.DrawText(CreateText(s, size), at);
    }

    private static void DrawTextRight(DrawingContext dc, string s, double size, Point rightTop)
    {
        if (string.IsNullOrEmpty(s)) return;

        var ft = CreateText(s, size);
        dc.DrawText(ft, new Point(rightTop.X - ft.Width, rightTop.Y));
    }
}