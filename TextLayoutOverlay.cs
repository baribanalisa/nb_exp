using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

// === ИСПРАВЛЕНИЕ КОНФЛИКТОВ ===
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
// ==============================

namespace NeuroBureau.Experiment;

/// <summary>
/// Overlay для визуализации разметки текста: границы слов, строк и привязки фиксаций
/// </summary>
public sealed class TextLayoutOverlay : FrameworkElement
{
    private TextLayoutResult? _layout;
    private List<FixationTextBinding>? _bindings;
    private int? _selectedWordIndex;
    private int? _hoveredWordIndex;

    // Настройки отображения
    public bool ShowWordBounds { get; set; } = true;
    public bool ShowLineBounds { get; set; } = true;
    public bool ShowFixations { get; set; } = true;
    public bool ShowBindingLines { get; set; } = true;
    public bool ShowWordNumbers { get; set; } = false;
    public bool ShowWordText { get; set; } = false;

    // Цвета
    public Color WordBoundsColor { get; set; } = Color.FromArgb(180, 0, 120, 255);
    public Color LineBoundsColor { get; set; } = Color.FromArgb(100, 0, 200, 100);
    public Color FixationColor { get; set; } = Color.FromArgb(180, 255, 100, 0);
    public Color BindingLineColor { get; set; } = Color.FromArgb(100, 128, 128, 128);
    public Color SelectedWordColor { get; set; } = Color.FromArgb(100, 255, 255, 0);
    public Color HoveredWordColor { get; set; } = Color.FromArgb(60, 255, 255, 0);

    // Размеры
    public double WordBoundsThickness { get; set; } = 1.5;
    public double LineBoundsThickness { get; set; } = 1.0;
    public double FixationRadius { get; set; } = 6;
    public double FontSize { get; set; } = 10;
    public FontFamily FontFamily { get; set; } = new("Segoe UI");

    /// <summary>
    /// Устанавливает разметку текста для отображения
    /// </summary>
    public void SetLayout(TextLayoutResult? layout)
    {
        _layout = layout;
        UpdateVisibility();
        InvalidateVisual();
    }

    /// <summary>
    /// Устанавливает привязки фиксаций к тексту
    /// </summary>
    public void SetBindings(List<FixationTextBinding>? bindings)
    {
        _bindings = bindings;
        InvalidateVisual();
    }

    /// <summary>
    /// Устанавливает выделенное слово
    /// </summary>
    public void SetSelectedWord(int? wordIndex)
    {
        if (_selectedWordIndex != wordIndex)
        {
            _selectedWordIndex = wordIndex;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Устанавливает слово под курсором
    /// </summary>
    public void SetHoveredWord(int? wordIndex)
    {
        if (_hoveredWordIndex != wordIndex)
        {
            _hoveredWordIndex = wordIndex;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Очищает все данные
    /// </summary>
    public void Clear()
    {
        _layout = null;
        _bindings = null;
        _selectedWordIndex = null;
        _hoveredWordIndex = null;
        UpdateVisibility();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_layout == null || _layout.IsEmpty) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 1. Рисуем границы строк
        if (ShowLineBounds)
        {
            DrawLineBounds(dc);
        }

        // 2. Рисуем выделение/hover слов
        DrawWordHighlights(dc);

        // 3. Рисуем границы слов
        if (ShowWordBounds)
        {
            DrawWordBounds(dc, dpi);
        }

        // 4. Рисуем привязки фиксаций
        if (ShowFixations && _bindings != null)
        {
            DrawFixations(dc);
        }
    }

    #region Drawing Methods

    private void DrawLineBounds(DrawingContext dc)
    {
        var pen = new Pen(new SolidColorBrush(LineBoundsColor), LineBoundsThickness)
        {
            DashStyle = DashStyles.Dot
        };
        pen.Freeze();

        foreach (var line in _layout!.Lines)
        {
            if (line.Words.Count == 0) continue;

            // Рисуем горизонтальную линию по центру строки
            double y = line.CenterY;
            double xStart = line.X - 10;
            double xEnd = line.X + line.Width + 10;

            dc.DrawLine(pen, new Point(xStart, y), new Point(xEnd, y));

            // Рисуем вертикальные границы строки
            dc.DrawLine(pen, new Point(xStart, line.Y), new Point(xStart, line.Y + line.Height));
            dc.DrawLine(pen, new Point(xEnd, line.Y), new Point(xEnd, line.Y + line.Height));
        }
    }

    private void DrawWordHighlights(DrawingContext dc)
    {
        // Выделенное слово
        if (_selectedWordIndex.HasValue && _selectedWordIndex.Value < _layout!.Words.Count)
        {
            var word = _layout.Words[_selectedWordIndex.Value];
            var brush = new SolidColorBrush(SelectedWordColor);
            brush.Freeze();
            dc.DrawRectangle(brush, null, word.Bounds);
        }

        // Слово под курсором
        if (_hoveredWordIndex.HasValue && _hoveredWordIndex.Value < _layout!.Words.Count &&
            _hoveredWordIndex != _selectedWordIndex)
        {
            var word = _layout.Words[_hoveredWordIndex.Value];
            var brush = new SolidColorBrush(HoveredWordColor);
            brush.Freeze();
            dc.DrawRectangle(brush, null, word.Bounds);
        }
    }

    private void DrawWordBounds(DrawingContext dc, double dpi)
    {
        var pen = new Pen(new SolidColorBrush(WordBoundsColor), WordBoundsThickness)
        {
            DashStyle = DashStyles.Dash
        };
        pen.Freeze();

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        foreach (var word in _layout!.Words)
        {
            // Рамка слова
            dc.DrawRectangle(null, pen, word.Bounds);

            // Номер слова (если включено)
            if (ShowWordNumbers)
            {
                var ft = new FormattedText(
                    word.Index.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    FontSize * 0.8,
                    new SolidColorBrush(WordBoundsColor),
                    dpi);

                dc.DrawText(ft, new Point(word.X + 2, word.Y - FontSize));
            }

            // Текст слова (если включено)
            if (ShowWordText)
            {
                var ft = new FormattedText(
                    word.Text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    FontSize,
                    Brushes.Black,
                    dpi);

                dc.DrawText(ft, new Point(word.X, word.Y));
            }
        }
    }

    private void DrawFixations(DrawingContext dc)
    {
        if (_bindings == null || _bindings.Count == 0) return;

        var fixBrush = new SolidColorBrush(FixationColor);
        fixBrush.Freeze();

        var fixPen = new Pen(fixBrush, 1.5);
        fixPen.Freeze();

        var linePen = new Pen(new SolidColorBrush(BindingLineColor), 1);
        linePen.Freeze();

        foreach (var binding in _bindings)
        {
            var fix = binding.Fixation;
            var fixPoint = new Point(fix.Xpx, fix.Ypx);

            // Рисуем линию к слову (если включено и есть привязка)
            if (ShowBindingLines && binding.WordIndex.HasValue &&
                binding.WordIndex.Value < _layout!.Words.Count)
            {
                var word = _layout.Words[binding.WordIndex.Value];
                var wordCenter = new Point(word.CenterX, word.CenterY);
                dc.DrawLine(linePen, fixPoint, wordCenter);
            }

            // Рисуем точку фиксации
            double radius = FixationRadius;

            // Размер зависит от длительности
            if (fix.DurSec > 0)
            {
                double k = Math.Clamp(fix.DurSec / 0.5, 0.5, 2.0);
                radius *= k;
            }

            dc.DrawEllipse(fixBrush, fixPen, fixPoint, radius, radius);

            // Если фиксация вне слов, показываем крестик
            if (!binding.WordIndex.HasValue)
            {
                var crossPen = new Pen(Brushes.Red, 1);
                crossPen.Freeze();
                dc.DrawLine(crossPen,
                    new Point(fix.Xpx - 4, fix.Ypx - 4),
                    new Point(fix.Xpx + 4, fix.Ypx + 4));
                dc.DrawLine(crossPen,
                    new Point(fix.Xpx + 4, fix.Ypx - 4),
                    new Point(fix.Xpx - 4, fix.Ypx + 4));
            }
        }
    }

    #endregion

    #region Hit Testing

    /// <summary>
    /// Находит слово по координатам точки
    /// </summary>
    public TextWord? HitTestWord(Point point)
    {
        if (_layout == null || _layout.IsEmpty) return null;
        return TextLayoutEngine.FindWordAt(_layout, point.X, point.Y, tolerance: 5);
    }

    /// <summary>
    /// Находит индекс слова по координатам
    /// </summary>
    public int? HitTestWordIndex(Point point)
    {
        var word = HitTestWord(point);
        return word?.Index;
    }

    #endregion

    private void UpdateVisibility()
    {
        Visibility = (_layout != null && !_layout.IsEmpty)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
