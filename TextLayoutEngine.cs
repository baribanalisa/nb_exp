using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

// === РАЗРЕШЕНИЕ КОНФЛИКТОВ ===
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
// =============================

namespace NeuroBureau.Experiment;

/// <summary>
/// Движок верстки текста для анализа чтения.
/// Использует WPF FormattedText для точного измерения ширины слов.
/// </summary>
public static class TextLayoutEngine
{
    private const double DefaultDpi = 96.0;

    /// <summary>
    /// Выполняет верстку текста согласно заданной конфигурации.
    /// </summary>
    /// <param name="config">Настройки верстки</param>
    /// <param name="dpi">DPI для рендеринга (по умолчанию 96)</param>
    /// <returns>Результат верстки с координатами слов и строк</returns>
    public static TextLayoutResult ComputeLayout(TextLayoutConfig config, double dpi = DefaultDpi)
    {
        var result = new TextLayoutResult { Config = config };

        if (string.IsNullOrWhiteSpace(config.Text))
            return result;

        var typeface = CreateTypeface(config.FontName);
        double fontSize = Math.Max(1, config.FontSizePx);
        double lineHeight = fontSize * Math.Max(1, config.LineSpacing);
        double maxWidth = config.MaxWidthPx > 0 ? config.MaxWidthPx : double.MaxValue;
        double spaceWidth = MeasureText(" ", typeface, fontSize, dpi);

        result.LineHeight = lineHeight;

        // Разбиваем текст на слова с сохранением позиций
        var wordInfos = SplitIntoWords(config.Text);

        double x = config.PaddingLeft;
        double y = config.PaddingTop;
        int lineIndex = 0;
        int globalWordIndex = 0;

        var currentLine = new TextLine
        {
            Index = lineIndex,
            Y = y,
            Height = lineHeight
        };
        result.Lines.Add(currentLine);

        double maxContentWidth = 0;
        var currentLineText = new List<string>();

        foreach (var (wordText, charStart, charEnd) in wordInfos)
        {
            double wordWidth = MeasureText(wordText, typeface, fontSize, dpi);
            double wordHeight = fontSize; // Приблизительная высота

            // Проверяем, нужен ли перенос строки
            bool needNewLine = false;

            // Принудительный перенос при \n уже обработан в SplitIntoWords
            // Здесь проверяем только перенос по ширине
            if (x + wordWidth > config.PaddingLeft + maxWidth && currentLine.Words.Count > 0)
            {
                needNewLine = true;
            }

            if (needNewLine)
            {
                // Сохраняем текст текущей строки
                currentLine.Text = string.Join(" ", currentLineText);
                maxContentWidth = Math.Max(maxContentWidth, x - spaceWidth - config.PaddingLeft);

                // Начинаем новую строку
                lineIndex++;
                y += lineHeight;
                x = config.PaddingLeft;

                currentLine = new TextLine
                {
                    Index = lineIndex,
                    Y = y,
                    Height = lineHeight
                };
                result.Lines.Add(currentLine);
                currentLineText.Clear();
            }

            // Применяем выравнивание (будет пересчитано в конце)
            var word = new TextWord
            {
                Index = globalWordIndex,
                LineIndex = lineIndex,
                Text = wordText,
                X = x,
                Y = y,
                Width = wordWidth,
                Height = wordHeight,
                CharStart = charStart,
                CharEnd = charEnd
            };

            result.Words.Add(word);
            currentLine.Words.Add(word);
            currentLineText.Add(wordText);

            x += wordWidth + spaceWidth;
            globalWordIndex++;
        }

        // Сохраняем текст последней строки
        if (currentLineText.Count > 0)
        {
            currentLine.Text = string.Join(" ", currentLineText);
            maxContentWidth = Math.Max(maxContentWidth, x - spaceWidth - config.PaddingLeft);
        }

        // Применяем выравнивание
        ApplyAlignment(result, config.Alignment, maxWidth, config.PaddingLeft);

        result.ContentWidth = result.Words.Count > 0
            ? result.Words.Max(w => w.X + w.Width)
            : 0;
        result.ContentHeight = y + lineHeight;

        return result;
    }

    /// <summary>
    /// Пересчитывает координаты слов для нового размера области
    /// </summary>
    public static void RecalculateForSize(TextLayoutResult layout, double newMaxWidth)
    {
        if (layout.IsEmpty) return;

        var newConfig = new TextLayoutConfig
        {
            Text = layout.Config.Text,
            FontName = layout.Config.FontName,
            FontSizePx = layout.Config.FontSizePx,
            LineSpacing = layout.Config.LineSpacing,
            MaxWidthPx = newMaxWidth,
            PaddingLeft = layout.Config.PaddingLeft,
            PaddingTop = layout.Config.PaddingTop,
            Alignment = layout.Config.Alignment
        };

        var newLayout = ComputeLayout(newConfig);

        layout.Lines.Clear();
        layout.Lines.AddRange(newLayout.Lines);
        layout.Words.Clear();
        layout.Words.AddRange(newLayout.Words);
        layout.ContentWidth = newLayout.ContentWidth;
        layout.ContentHeight = newLayout.ContentHeight;
        layout.LineHeight = newLayout.LineHeight;
        layout.Config = newConfig;
    }

    /// <summary>
    /// Находит слово по координатам точки
    /// </summary>
    public static TextWord? FindWordAt(TextLayoutResult layout, double x, double y, double tolerance = 0)
    {
        if (layout.IsEmpty) return null;

        TextWord? nearest = null;
        double minDist = double.MaxValue;

        foreach (var word in layout.Words)
        {
            var bounds = tolerance > 0
                ? new Rect(word.X - tolerance, word.Y - tolerance,
                           word.Width + tolerance * 2, word.Height + tolerance * 2)
                : word.Bounds;

            if (bounds.Contains(new Point(x, y)))
            {
                double dist = DistanceToWord(x, y, word);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = word;
                }
            }
        }

        return nearest;
    }

    /// <summary>
    /// Находит строку по Y-координате
    /// </summary>
    public static TextLine? FindLineAt(TextLayoutResult layout, double y)
    {
        if (layout.IsEmpty) return null;

        foreach (var line in layout.Lines)
        {
            if (y >= line.Y && y < line.Y + line.Height)
                return line;
        }

        // Если не попали точно, возвращаем ближайшую
        return layout.Lines
            .OrderBy(l => Math.Abs(l.CenterY - y))
            .FirstOrDefault();
    }

    /// <summary>
    /// Вычисляет расстояние от точки до слова
    /// </summary>
    public static double DistanceToWord(double x, double y, TextWord word)
    {
        double dx = 0, dy = 0;

        if (x < word.X)
            dx = word.X - x;
        else if (x > word.X + word.Width)
            dx = x - (word.X + word.Width);

        if (y < word.Y)
            dy = word.Y - y;
        else if (y > word.Y + word.Height)
            dy = y - (word.Y + word.Height);

        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Проверяет, находится ли точка внутри слова
    /// </summary>
    public static bool IsPointInWord(double x, double y, TextWord word)
    {
        return x >= word.X && x <= word.X + word.Width &&
               y >= word.Y && y <= word.Y + word.Height;
    }

    #region Private Methods

    private static Typeface CreateTypeface(string fontName)
    {
        try
        {
            return new Typeface(fontName);
        }
        catch
        {
            return new Typeface("Segoe UI");
        }
    }

    private static double MeasureText(string text, Typeface typeface, double fontSize, double dpi)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            dpi);

        return ft.Width;
    }

    private static List<(string word, int charStart, int charEnd)> SplitIntoWords(string text)
    {
        var result = new List<(string, int, int)>();
        if (string.IsNullOrEmpty(text)) return result;

        int i = 0;
        int n = text.Length;

        while (i < n)
        {
            // Пропускаем пробелы
            while (i < n && char.IsWhiteSpace(text[i]) && text[i] != '\n')
                i++;

            if (i >= n) break;

            // Обработка переноса строки как разделителя
            if (text[i] == '\n')
            {
                i++;
                continue;
            }

            // Начало слова
            int wordStart = i;

            // Ищем конец слова
            while (i < n && !char.IsWhiteSpace(text[i]))
                i++;

            int wordEnd = i;

            if (wordEnd > wordStart)
            {
                string word = text.Substring(wordStart, wordEnd - wordStart);
                result.Add((word, wordStart, wordEnd));
            }
        }

        return result;
    }

    private static void ApplyAlignment(TextLayoutResult result, TextLayoutAlignment alignment, double maxWidth, double paddingLeft)
    {
        if (alignment == TextLayoutAlignment.Left || !double.IsFinite(maxWidth))
            return;

        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;

            double lineWidth = line.Width;
            double shift = 0;

            if (alignment == TextLayoutAlignment.Center)
            {
                shift = (maxWidth - lineWidth) / 2;
            }
            else if (alignment == TextLayoutAlignment.Right)
            {
                shift = maxWidth - lineWidth;
            }

            if (shift > 0)
            {
                foreach (var word in line.Words)
                {
                    word.X += shift;
                }
            }
        }
    }

    #endregion
}
