
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

// === РЕШЕНИЕ КОНФЛИКТОВ ===
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
// ==========================

namespace NeuroBureau.Experiment;

public static class AoiGeometry
{
    public static bool IsPointInAoi(Point p, AoiElement aoi, double w, double h)
    {
        if (aoi.NormalizedPoints.Count < 3 && (aoi.Type == AoiType.Polygon)) return false;
        if (aoi.NormalizedPoints.Count < 2 && (aoi.Type == AoiType.Rectangle || aoi.Type == AoiType.Ellipse)) return false;

        // Переводим точки AOI из 0..1 в реальные пиксели
        var pixels = aoi.NormalizedPoints.Select(np => new Point(np.X * w, np.Y * h)).ToList();

        // Используем встроенную геометрию WPF для проверки попадания.
        // Это гарантирует 100% совпадение с тем, что нарисовано на экране.
        
        if (aoi.Type == AoiType.Rectangle)
        {
            var r = new Rect(pixels[0], pixels[1]);
            return r.Contains(p);
        }
        else if (aoi.Type == AoiType.Ellipse)
        {
            var r = new Rect(pixels[0], pixels[1]);
            var geom = new EllipseGeometry(r);
            return geom.FillContains(p);
        }
        else // Polygon или Polyline
        {
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                // isFilled = true, isClosed = true (замыкаем фигуру)
                ctx.BeginFigure(pixels[0], true, true);
                ctx.PolyLineTo(pixels.Skip(1).ToList(), true, false);
            }
            
            // Проверка попадания точки внутрь замкнутой фигуры
            return geom.FillContains(p);
        }
    }

    private static Rect GetBoundingRect(List<Point> points)
    {
        if (points.Count < 2) return Rect.Empty;
        return new Rect(points[0], points[1]);
    }

    private static bool IsPointInEllipse(Point p, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return false;
        
        double cx = r.X + r.Width / 2.0;
        double cy = r.Y + r.Height / 2.0;
        double rx = r.Width / 2.0;
        double ry = r.Height / 2.0;

        double dx = p.X - cx;
        double dy = p.Y - cy;

        return ((dx * dx) / (rx * rx) + (dy * dy) / (ry * ry)) <= 1.0;
    }
    // AoiGeometry.cs (добавить внутрь класса AoiGeometry)

    // Формула шнурков (Shoelace formula) для полигона
    private static double CalculatePolygonArea(List<Point> points)
    {
        double area = 0.0;
        int j = points.Count - 1;
        for (int i = 0; i < points.Count; i++)
        {
            area += (points[j].X + points[i].X) * (points[j].Y - points[i].Y);
            j = i;
        }
        return Math.Abs(area / 2.0);
    }
    private static bool IsPointInPolygon(Point p, List<Point> poly)
    {
        bool inside = false;
        int j = poly.Count - 1;
        for (int i = 0; i < poly.Count; i++)
        {
            if (((poly[i].Y < p.Y && poly[j].Y >= p.Y) || (poly[j].Y < p.Y && poly[i].Y >= p.Y)) &&
                (poly[i].X + (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) * (poly[j].X - poly[i].X) < p.X))
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }
     // Расчет площади (в пикселях)
    public static double CalculateAreaPx(AoiElement aoi, double w, double h)
    {
        if (aoi.NormalizedPoints.Count < 2) return 0;
        
        // Переводим 0..1 в реальные пиксели
        var pixels = aoi.NormalizedPoints.Select(np => new Point(np.X * w, np.Y * h)).ToList();

        if (aoi.Type == AoiType.Rectangle)
        {
            var r = new Rect(pixels[0], pixels[1]);
            return r.Width * r.Height;
        }
        else if (aoi.Type == AoiType.Ellipse)
        {
            var r = new Rect(pixels[0], pixels[1]);
            // Площадь эллипса = π * a * b
            return System.Math.PI * (r.Width / 2.0) * (r.Height / 2.0);
        }
        else // Polygon или Polyline
        {
            // Формула площади Гаусса (метод шнурков)
            // Идеально подходит для любых полигонов
            double area = 0.0;
            int j = pixels.Count - 1;

            for (int i = 0; i < pixels.Count; i++)
            {
                area += (pixels[j].X + pixels[i].X) * (pixels[j].Y - pixels[i].Y);
                j = i;
            }

            return System.Math.Abs(area / 2.0);
        }
    }
}