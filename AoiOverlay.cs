
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

// === РЕШЕНИЕ КОНФЛИКТОВ ===
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
// ==========================

namespace NeuroBureau.Experiment;

public class AoiOverlay : FrameworkElement
{
    private List<AoiElement> _aois = new();
    private AoiMetricsResult[]? _metrics;

    public AoiElement? GhostAoi { get; set; }

    public void SetData(List<AoiElement> aois, AoiMetricsResult[]? metrics = null)
    {
        _aois = aois ?? new List<AoiElement>();
        _metrics = metrics;
        InvalidateVisual();
    }
    
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
         // === ДОБАВИТЬ ЭТУ СТРОКУ ===
    // Рисуем невидимый прямоугольник во всю область, чтобы ловить клики мыши
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
    // ===========================
        
        if (w <= 0 || h <= 0) return;

        foreach (var aoi in _aois)
        {
            DrawSingleAoi(dc, aoi, w, h, isGhost: false);
        }

        if (GhostAoi != null)
        {
            DrawSingleAoi(dc, GhostAoi, w, h, isGhost: true);
        }
    }

    private void DrawSingleAoi(DrawingContext dc, AoiElement aoi, double w, double h, bool isGhost)
    {
        if (aoi.NormalizedPoints.Count == 0) return;

        var pixels = aoi.NormalizedPoints.Select(np => new Point(np.X * w, np.Y * h)).ToList();

        var color = aoi.Color;
        var brush = new SolidColorBrush(color);
        brush.Opacity = isGhost ? 0.3 : 0.15;
        brush.Freeze();
        
        var pen = new Pen(new SolidColorBrush(color), aoi.LineWidth);
        if (isGhost) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();

        Geometry? geom = null;
        Point center = new Point(0, 0);

        switch (aoi.Type)
        {
            case AoiType.Rectangle:
            case AoiType.Ellipse:
                if (pixels.Count < 2) return;
                var r = new Rect(pixels[0], pixels[1]);
                center = new Point(r.X + r.Width/2, r.Y + r.Height/2);
                
                if (aoi.Type == AoiType.Rectangle)
                    geom = new RectangleGeometry(r);
                else
                    geom = new EllipseGeometry(r);
                break;

            case AoiType.Polygon:
            case AoiType.Polyline:
                if (pixels.Count < 1) return;
                var stream = new StreamGeometry();
                using (var ctx = stream.Open())
                {
                    ctx.BeginFigure(pixels[0], true, true);
                    if (pixels.Count > 1)
                        ctx.PolyLineTo(pixels.Skip(1).ToList(), true, false);
                }
                geom = stream;
                
                double cx = 0, cy = 0;
                foreach (var p in pixels) { cx += p.X; cy += p.Y; }
                center = new Point(cx / pixels.Count, cy / pixels.Count);
                break;
        }

        if (geom != null)
        {
            dc.DrawGeometry(brush, pen, geom);

            if (!isGhost)
            {
                DrawTextLabel(dc, aoi.Name, pixels[0], color, aoi.FontSize);
            }

            if (!isGhost && aoi.ShowCallout)
            {
                DrawCallout(dc, aoi, center);
            }
        }
        
        if (isGhost && (aoi.Type == AoiType.Polyline || aoi.Type == AoiType.Polygon))
        {
             foreach(var p in pixels) dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black,1), p, 3, 3);
        }
    }

     private void DrawTextLabel(DrawingContext dc, string text, Point pos, Color color, double size)
    {
        // ИСПРАВЛЕНО: System.Windows.FlowDirection.LeftToRight
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), null, 
            new Rect(pos.X, pos.Y - size - 4, ft.Width + 4, ft.Height));
        
        dc.DrawText(ft, new Point(pos.X + 2, pos.Y - size - 4));
    }

    private void DrawCallout(DrawingContext dc, AoiElement aoi, Point center)
    {
        var m = _metrics?.FirstOrDefault(x => x.AoiName == aoi.Name);
        if (m == null) return;

        var lines = new List<string>
        {
            $"AOI: {aoi.Name}",
            $"Fix: {m.FixationCount}",
            $"Dwell: {m.TotalDwellTime:F2}s"
        };
        if (m.TimeToFirstFixation >= 0) 
            lines.Add($"TTFF: {m.TimeToFirstFixation:F2}s");

        var fullText = string.Join("\n", lines);

        // ИСПРАВЛЕНО: System.Windows.FlowDirection.LeftToRight
        var ft = new FormattedText(fullText, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
             new Typeface("Segoe UI"), 11, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        Point calloutPos = new Point(center.X + aoi.CalloutOffsetX, center.Y + aoi.CalloutOffsetY);
        
        dc.DrawLine(new Pen(Brushes.Gray, 1), center, calloutPos);

        dc.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.Black, 1), 
            new Rect(calloutPos.X, calloutPos.Y, ft.Width + 6, ft.Height + 4));

        dc.DrawText(ft, new Point(calloutPos.X + 3, calloutPos.Y + 2));
    }
}