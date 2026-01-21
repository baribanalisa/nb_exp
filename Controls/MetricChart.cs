using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private string _emptyText = "—";

    // Свойства для скроллинга и зума
    private double _visibleTMin, _visibleTMax;
    private double _totalTMin, _totalTMax;
    private double _zoomFactor = 1.0;
    private bool _isDragging = false;
    private double _dragStartX = 0;
    
    private const double Pad = 10;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 10.0;

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
    }

    public void Clear(string? text = null)
    {
        _series = null;
        _fixBands = null;
        _emptyText = string.IsNullOrWhiteSpace(text) ? "—" : text;
        InvalidateVisual();
    }

    public void SetData(
        IReadOnlyList<MetricSeries> series,
        double tMin, double tMax,
        double yMin, double yMax,
        double threshold,
        string title,
        IReadOnlyList<(double StartSec, double EndSec)>? fixBands = null)
    {
        _series = series;
        _fixBands = fixBands;

        _totalTMin = tMin;
        _totalTMax = Math.Max(tMin + 1e-6, tMax);

        _tMin = tMin;
        _tMax = Math.Max(tMin + 1e-6, tMax);

        _yMin = yMin;
        _yMax = Math.Max(yMin + 1e-6, yMax);

        _threshold = threshold;
        _title = title ?? "";

        _emptyText = "—";
        
        // Инициализация видимого диапазона
        _zoomFactor = 1.0;
        _visibleTMin = _totalTMin;
        _visibleTMax = _totalTMax;
        
        InvalidateVisual();
    }

    public void SetData(
        IReadOnlyList<MetricPoint> points,
        double tMin, double tMax,
        double yMin, double yMax,
        double threshold,
        string title,
        IReadOnlyList<(double StartSec, double EndSec)>? fixBands = null)
    {
        SetData(new List<MetricSeries> { new(points, ((SolidColorBrush)Line).Color) },
            tMin, tMax, yMin, yMax, threshold, title, fixBands);
    }

    // Добавляем обработчики событий
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        
        if (_totalTMax <= _totalTMin) return;
        
        var zoomFactor = e.Delta > 0 ? 0.8 : 1.25; // Уменьшаем/увеличиваем масштаб
        var mousePos = e.GetPosition(this);
        
        ZoomAt(mousePos, zoomFactor);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            _dragStartX = e.GetPosition(this).X;
            CaptureMouse();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        
        if (e.LeftButton == MouseButtonState.Released)
        {
            _isDragging = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            var currentPos = e.GetPosition(this);
            var deltaX = currentPos.X - _dragStartX;
            _dragStartX = currentPos.X;
            
            ScrollByDelta(deltaX);
        }
    }

    private void ZoomAt(Point mousePos, double zoomFactor)
    {
        var newZoomFactor = Math.Max(MinZoom, Math.Min(MaxZoom, _zoomFactor * zoomFactor));
        
        if (Math.Abs(newZoomFactor - _zoomFactor) < 0.01) return;
        
        var plot = GetPlotRect();
        var relativeX = (mousePos.X - plot.Left) / plot.Width;
        
        var totalRange = _totalTMax - _totalTMin;
        var oldVisibleRange = totalRange * _zoomFactor;
        var newVisibleRange = totalRange * newZoomFactor;
        
        var centerTime = _visibleTMin + relativeX * oldVisibleRange;
        var newVisibleTMin = centerTime - relativeX * newVisibleRange;
        var newVisibleTMax = newVisibleTMin + newVisibleRange;
        
        // Ограничиваем границы
        if (newVisibleTMin < _totalTMin)
        {
            newVisibleTMin = _totalTMin;
            newVisibleTMax = newVisibleTMin + newVisibleRange;
        }
        if (newVisibleTMax > _totalTMax)
        {
            newVisibleTMax = _totalTMax;
            newVisibleTMin = newVisibleTMax - newVisibleRange;
        }
        
        _zoomFactor = newZoomFactor;
        _visibleTMin = newVisibleTMin;
        _visibleTMax = newVisibleTMax;
        
        InvalidateVisual();
    }

    private void ScrollByDelta(double deltaX)
    {
        if (_totalTMax <= _totalTMin) return;
        
        var totalRange = _totalTMax - _totalTMin;
        var visibleRange = _visibleTMax - _visibleTMin;
        var scrollAmount = -deltaX / ActualWidth * totalRange;
        
        var newTMin = _visibleTMin + scrollAmount;
        var newTMax = _visibleTMax + scrollAmount;
        
        // Ограничиваем границы
        if (newTMin < _totalTMin)
        {
            newTMin = _totalTMin;
            newTMax = newTMin + visibleRange;
        }
        if (newTMax > _totalTMax)
        {
            newTMax = _totalTMax;
            newTMin = newTMax - visibleRange;
        }
        
        _visibleTMin = newTMin;
        _visibleTMax = newTMax;
        
        InvalidateVisual();
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
                var x1 = XVisible(s, plot);
                var x2 = XVisible(e, plot);
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
                    if (p.TimeSec < _visibleTMin || p.TimeSec > _visibleTMax) continue;
                    if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

                    var pt = new Point(XVisible(p.TimeSec, plot), Y(p.Value, plot));
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

        // y labels (min/max)
        DrawText(dc, $"{_yMax:0}", 11, new Point(plot.Right - 42, plot.Top - 2));
        DrawText(dc, $"{_yMin:0}", 11, new Point(plot.Right - 42, plot.Bottom - 14));

        // x labels (min/max)
        DrawText(dc, $"{_visibleTMin:0.0}s", 10, new Point(plot.Left, plot.Bottom + 2));
        DrawText(dc, $"{_visibleTMax:0.0}s", 10, new Point(plot.Right - 50, plot.Bottom + 2));
    }

    private double X(double t, Rect plot)
        => plot.Left + (t - _tMin) / (_tMax - _tMin) * plot.Width;

    private double XVisible(double t, Rect plot)
        => plot.Left + (t - _visibleTMin) / (_visibleTMax - _visibleTMin) * plot.Width;

    private double Y(double v, Rect plot)
        => plot.Bottom - (v - _yMin) / (_yMax - _yMin) * plot.Height;

    private static void DrawText(DrawingContext dc, string s, double size, Point at)
    {
        if (string.IsNullOrEmpty(s)) return;

        var ft = new FormattedText(
            s,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            Text,
            VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);

        dc.DrawText(ft, at);
    }
}
