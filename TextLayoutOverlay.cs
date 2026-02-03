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
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
// ==============================

namespace NeuroBureau.Experiment;

/// <summary>
/// Overlay для визуализации разметки текста: границы слов, строк и фиксаций (до/после коррекции)
/// </summary>
public sealed class TextLayoutOverlay : FrameworkElement
{
    private TextLayoutResult? _layout;
    private List<FixationTextBinding>? _bindings;
    private int? _selectedWordIndex;
    private int? _hoveredWordIndex;

    // Оригинальные фиксации (до коррекции)
    private IReadOnlyList<Fixation>? _originalFixations;
    // Скорректированные фиксации (после drift correction)
    private IReadOnlyList<Fixation>? _correctedFixations;

    // Режим отображения
    public bool ShowOriginalFixations { get; set; } = true;
    public bool ShowCorrectedFixations { get; set; } = true;
    public bool ShowScanPath { get; set; } = true;

    // Настройки отображения границ
    public bool ShowWordBounds { get; set; } = true;
    public bool ShowLineBounds { get; set; } = true;
    public bool ShowBindingLines { get; set; } = false;
    public bool ShowWordNumbers { get; set; } = false;
    public bool ShowWordText { get; set; } = true;

    // Цвета
    public Color WordBoundsColor { get; set; } = Color.FromArgb(180, 0, 120, 255);
    public Color LineBoundsColor { get; set; } = Color.FromArgb(100, 0, 200, 100);
    public Color OriginalFixationColor { get; set; } = Color.FromArgb(200, 60, 214, 231); // Cyan - как в Python
    public Color CorrectedFixationColor { get; set; } = Color.FromArgb(220, 255, 100, 0); // Orange
    public Color ScanPathColor { get; set; } = Color.FromArgb(120, 100, 100, 100);
    public Color BindingLineColor { get; set; } = Color.FromArgb(80, 128, 128, 128);
    public Color SelectedWordColor { get; set; } = Color.FromArgb(100, 255, 255, 0);
    public Color HoveredWordColor { get; set; } = Color.FromArgb(60, 255, 255, 0);
    public Color TextColor { get; set; } = Color.FromArgb(255, 0, 0, 0);

    // Размеры
    public double WordBoundsThickness { get; set; } = 1.5;
    public double LineBoundsThickness { get; set; } = 1.0;
    public double FixationBaseRadius { get; set; } = 8;
    public double ScanPathThickness { get; set; } = 1.5;
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
    /// Устанавливает оригинальные фиксации (до коррекции дрифта)
    /// </summary>
    public void SetOriginalFixations(IReadOnlyList<Fixation>? fixations)
    {
        _originalFixations = fixations;
        InvalidateVisual();
    }

    /// <summary>
    /// Устанавливает скорректированные фиксации (после drift correction)
    /// </summary>
    public void SetCorrectedFixations(IReadOnlyList<Fixation>? fixations)
    {
        _correctedFixations = fixations;
        InvalidateVisual();
    }

    /// <summary>
    /// Устанавливает привязки фиксаций к тексту (для отображения связей)
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
        _originalFixations = null;
        _correctedFixations = null;
        _selectedWordIndex = null;
        _hoveredWordIndex = null;
        UpdateVisibility();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Всегда рисуем фиксации, даже если нет layout
        var hasFixations = (_originalFixations?.Count > 0) || (_correctedFixations?.Count > 0);
        var hasLayout = _layout != null && !_layout.IsEmpty;

        if (!hasFixations && !hasLayout) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 1. Рисуем границы строк (если есть layout)
        if (hasLayout && ShowLineBounds)
        {
            DrawLineBounds(dc);
        }

        // 2. Рисуем выделение/hover слов
        if (hasLayout)
        {
            DrawWordHighlights(dc);
        }

        // 3. Рисуем границы слов и текст
        if (hasLayout && ShowWordBounds)
        {
            DrawWordBounds(dc, dpi);
        }

        // 4. Рисуем оригинальные фиксации (до коррекции) - CYAN
        if (ShowOriginalFixations && _originalFixations?.Count > 0)
        {
            DrawFixationsWithScanPath(dc, _originalFixations, OriginalFixationColor, 0.7);
        }

        // 5. Рисуем скорректированные фиксации - ORANGE
        if (ShowCorrectedFixations && _correctedFixations?.Count > 0)
        {
            DrawFixationsWithScanPath(dc, _correctedFixations, CorrectedFixationColor, 1.0);
        }

        // 6. Рисуем линии привязки к словам (если включено)
        if (ShowBindingLines && _bindings != null && hasLayout)
        {
            DrawBindingLines(dc);
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
        var textBrush = new SolidColorBrush(TextColor);
        textBrush.Freeze();

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
                var textTypeface = new Typeface(
                    new FontFamily(_layout.Config.FontName),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

                // Используем высоту строки из OCR (line.Height), чтобы сохранить
                // реальные различия размера текста между строками и избежать
                // шума от отдельных слов.
                double fontSize = _layout.Config.FontSizePx;
                if (word.LineIndex >= 0 && word.LineIndex < _layout.Lines.Count)
                {
                    var lineHeight = _layout.Lines[word.LineIndex].Height;
                    if (lineHeight > 0)
                    {
                        fontSize = lineHeight;
                    }
                }

                var ft = new FormattedText(
                    word.Text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    textTypeface,
                    fontSize,
                    textBrush,
                    dpi);

                dc.DrawText(ft, new Point(word.X, word.Y));
            }
        }
    }

    private void DrawFixationsWithScanPath(DrawingContext dc, IReadOnlyList<Fixation> fixations, Color color, double opacity)
    {
        if (fixations.Count == 0) return;

        var fixBrush = new SolidColorBrush(Color.FromArgb((byte)(color.A * opacity), color.R, color.G, color.B));
        fixBrush.Freeze();

        var fixPen = new Pen(fixBrush, 1.5);
        fixPen.Freeze();

        // Scan path линии
        if (ShowScanPath && fixations.Count > 1)
        {
            var pathColor = Color.FromArgb((byte)(ScanPathColor.A * opacity), color.R, color.G, color.B);
            var pathPen = new Pen(new SolidColorBrush(pathColor), ScanPathThickness);
            pathPen.Freeze();

            for (int i = 1; i < fixations.Count; i++)
            {
                var prev = fixations[i - 1];
                var curr = fixations[i];
                dc.DrawLine(pathPen, new Point(prev.Xpx, prev.Ypx), new Point(curr.Xpx, curr.Ypx));
            }
        }

        // Точки фиксаций
        foreach (var fix in fixations)
        {
            var fixPoint = new Point(fix.Xpx, fix.Ypx);

            // Размер зависит от длительности
            double radius = FixationBaseRadius;
            if (fix.DurSec > 0)
            {
                double k = Math.Clamp(fix.DurSec / 0.3, 0.5, 2.5);
                radius *= k;
            }

            dc.DrawEllipse(fixBrush, fixPen, fixPoint, radius, radius);
        }
    }

    private void DrawBindingLines(DrawingContext dc)
    {
        if (_bindings == null || _bindings.Count == 0 || _layout == null) return;

        var linePen = new Pen(new SolidColorBrush(BindingLineColor), 1)
        {
            DashStyle = DashStyles.Dot
        };
        linePen.Freeze();

        foreach (var binding in _bindings)
        {
            if (!binding.WordIndex.HasValue) continue;
            if (binding.WordIndex.Value >= _layout.Words.Count) continue;

            var fix = binding.Fixation;
            var word = _layout.Words[binding.WordIndex.Value];

            var fixPoint = new Point(fix.Xpx, fix.Ypx);
            var wordCenter = new Point(word.CenterX, word.CenterY);

            dc.DrawLine(linePen, fixPoint, wordCenter);
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
        var hasContent = (_layout != null && !_layout.IsEmpty) ||
                        (_originalFixations?.Count > 0) ||
                        (_correctedFixations?.Count > 0);

        Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
    }
}
