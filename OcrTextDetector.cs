using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

// Разрешение конфликта имён
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace NeuroBureau.Experiment;

/// <summary>
/// Автоматическое распознавание текста на изображении стимула с помощью Windows OCR.
/// Определяет текст, его расположение, размер шрифта, межстрочный интервал.
/// </summary>
public static class OcrTextDetector
{
    /// <summary>
    /// Распознаёт текст на изображении и создаёт конфигурацию для TextLayoutEngine
    /// </summary>
    /// <param name="imagePath">Путь к изображению стимула</param>
    /// <param name="language">Язык OCR (ru, en, etc.)</param>
    /// <returns>Конфигурация текста или null при ошибке</returns>
    public static async Task<OcrDetectionResult?> DetectTextAsync(string imagePath, string language = "ru")
    {
        if (!File.Exists(imagePath))
            return null;

        try
        {
            // Загружаем изображение
            using var stream = File.OpenRead(imagePath);
            var decoder = await WinBitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            // Получаем OCR engine
            var ocrLanguage = new Windows.Globalization.Language(language);
            OcrEngine? engine = null;

            if (OcrEngine.IsLanguageSupported(ocrLanguage))
            {
                engine = OcrEngine.TryCreateFromLanguage(ocrLanguage);
            }

            // Fallback на английский или доступный язык
            if (engine == null)
            {
                engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en"));
            }
            if (engine == null)
            {
                var availableLangs = OcrEngine.AvailableRecognizerLanguages;
                if (availableLangs.Count > 0)
                    engine = OcrEngine.TryCreateFromLanguage(availableLangs[0]);
            }

            if (engine == null)
                return null;

            // Выполняем OCR
            var ocrResult = await engine.RecognizeAsync(softwareBitmap);

            if (ocrResult == null || ocrResult.Lines.Count == 0)
                return null;

            // Конвертируем результат
            return ConvertOcrResult(ocrResult, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OCR error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Распознаёт текст из BitmapImage
    /// </summary>
    public static async Task<OcrDetectionResult?> DetectTextAsync(BitmapImage bitmapImage, string language = "ru")
    {
        if (bitmapImage?.UriSource == null)
            return null;

        var path = bitmapImage.UriSource.LocalPath;
        return await DetectTextAsync(path, language);
    }

    /// <summary>
    /// Конвертирует результат Windows OCR в нашу модель
    /// </summary>
    private static OcrDetectionResult ConvertOcrResult(OcrResult ocrResult, int imageWidth, int imageHeight)
    {
        var result = new OcrDetectionResult
        {
            ImageWidth = imageWidth,
            ImageHeight = imageHeight
        };

        var allWords = new List<OcrWord>();
        var lines = new List<OcrLine>();

        foreach (var ocrLine in ocrResult.Lines)
        {
            var line = new OcrLine
            {
                Index = lines.Count,
                Text = ocrLine.Text
            };

            double minX = double.MaxValue, maxX = 0;
            double minY = double.MaxValue, maxY = 0;

            foreach (var ocrWord in ocrLine.Words)
            {
                var word = new OcrWord
                {
                    Index = allWords.Count,
                    LineIndex = line.Index,
                    Text = ocrWord.Text,
                    X = ocrWord.BoundingRect.X,
                    Y = ocrWord.BoundingRect.Y,
                    Width = ocrWord.BoundingRect.Width,
                    Height = ocrWord.BoundingRect.Height
                };

                allWords.Add(word);
                line.Words.Add(word);

                minX = Math.Min(minX, word.X);
                maxX = Math.Max(maxX, word.X + word.Width);
                minY = Math.Min(minY, word.Y);
                maxY = Math.Max(maxY, word.Y + word.Height);
            }

            if (line.Words.Count > 0)
            {
                line.X = minX;
                line.Y = minY;
                line.Width = maxX - minX;
                line.Height = maxY - minY;
                lines.Add(line);
            }
        }

        result.Words = allWords;
        result.Lines = lines;

        // Анализируем параметры текста
        AnalyzeTextParameters(result);

        return result;
    }

    /// <summary>
    /// Анализирует параметры текста (размер шрифта, межстрочный интервал, отступы)
    /// </summary>
    private static void AnalyzeTextParameters(OcrDetectionResult result)
    {
        if (result.Lines.Count == 0)
            return;

        // 1. Определяем размер шрифта (медиана высоты слов)
        var wordHeights = result.Words.Select(w => w.Height).OrderBy(h => h).ToList();
        if (wordHeights.Count > 0)
        {
            result.EstimatedFontSize = wordHeights[wordHeights.Count / 2];
        }

        // 2. Определяем межстрочный интервал
        if (result.Lines.Count > 1)
        {
            var lineGaps = new List<double>();
            for (int i = 1; i < result.Lines.Count; i++)
            {
                var prevLine = result.Lines[i - 1];
                var currLine = result.Lines[i];

                // Расстояние между центрами строк
                double prevCenterY = prevLine.Y + prevLine.Height / 2;
                double currCenterY = currLine.Y + currLine.Height / 2;
                lineGaps.Add(currCenterY - prevCenterY);
            }

            if (lineGaps.Count > 0)
            {
                lineGaps.Sort();
                double medianGap = lineGaps[lineGaps.Count / 2];
                result.EstimatedLineHeight = medianGap;

                if (result.EstimatedFontSize > 0)
                {
                    result.EstimatedLineSpacing = medianGap / result.EstimatedFontSize;
                }
            }
        }
        else
        {
            result.EstimatedLineHeight = result.EstimatedFontSize * 1.5;
            result.EstimatedLineSpacing = 1.5;
        }

        // 3. Определяем отступы (минимальные X и Y всех слов)
        if (result.Words.Count > 0)
        {
            result.EstimatedPaddingLeft = result.Words.Min(w => w.X);
            result.EstimatedPaddingTop = result.Words.Min(w => w.Y);
        }

        // 4. Определяем выравнивание
        result.EstimatedAlignment = DetectAlignment(result);

        // 5. Собираем полный текст
        result.FullText = string.Join("\n", result.Lines.Select(l => l.Text));
    }

    /// <summary>
    /// Определяет выравнивание текста
    /// </summary>
    private static TextLayoutAlignment DetectAlignment(OcrDetectionResult result)
    {
        if (result.Lines.Count < 2)
            return TextLayoutAlignment.Left;

        // Анализируем X-координаты начала строк
        var lineStarts = result.Lines.Select(l => l.X).ToList();
        var lineEnds = result.Lines.Select(l => l.X + l.Width).ToList();
        var lineCenters = result.Lines.Select(l => l.X + l.Width / 2).ToList();

        double startVariance = CalculateVariance(lineStarts);
        double endVariance = CalculateVariance(lineEnds);
        double centerVariance = CalculateVariance(lineCenters);

        // Если начала строк выровнены - выравнивание влево
        if (startVariance < endVariance && startVariance < centerVariance)
            return TextLayoutAlignment.Left;

        // Если концы выровнены - вправо
        if (endVariance < startVariance && endVariance < centerVariance)
            return TextLayoutAlignment.Right;

        // Если центры выровнены - по центру
        if (centerVariance < startVariance && centerVariance < endVariance)
            return TextLayoutAlignment.Center;

        return TextLayoutAlignment.Left;
    }

    private static double CalculateVariance(List<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / values.Count;
    }

    /// <summary>
    /// Создаёт TextLayoutConfig на основе результатов OCR
    /// </summary>
    public static TextLayoutConfig CreateLayoutConfig(OcrDetectionResult ocrResult)
    {
        return new TextLayoutConfig
        {
            Text = ocrResult.FullText,
            FontName = "Segoe UI", // OCR не определяет шрифт, используем дефолтный
            FontSizePx = ocrResult.EstimatedFontSize,
            LineSpacing = ocrResult.EstimatedLineSpacing,
            PaddingLeft = ocrResult.EstimatedPaddingLeft,
            PaddingTop = ocrResult.EstimatedPaddingTop,
            Alignment = ocrResult.EstimatedAlignment,
            MaxWidthPx = ocrResult.ImageWidth - ocrResult.EstimatedPaddingLeft * 2
        };
    }

    /// <summary>
    /// Создаёт TextLayoutResult напрямую из OCR (без пересчёта через FormattedText)
    /// Использует реальные координаты слов с изображения
    /// </summary>
    public static TextLayoutResult CreateLayoutResultFromOcr(OcrDetectionResult ocrResult)
    {
        var config = CreateLayoutConfig(ocrResult);
        var result = new TextLayoutResult
        {
            Config = config,
            ContentWidth = ocrResult.Words.Count > 0 ? ocrResult.Words.Max(w => w.X + w.Width) : 0,
            ContentHeight = ocrResult.Lines.Count > 0 ? ocrResult.Lines.Max(l => l.Y + l.Height) : 0,
            LineHeight = ocrResult.EstimatedLineHeight
        };

        // Конвертируем OCR строки в TextLine
        foreach (var ocrLine in ocrResult.Lines)
        {
            var textLine = new TextLine
            {
                Index = ocrLine.Index,
                Y = ocrLine.Y,
                Height = ocrLine.Height,
                Text = ocrLine.Text
            };

            // Конвертируем OCR слова в TextWord
            foreach (var ocrWord in ocrLine.Words)
            {
                var textWord = new TextWord
                {
                    Index = ocrWord.Index,
                    LineIndex = ocrWord.LineIndex,
                    Text = ocrWord.Text,
                    X = ocrWord.X,
                    Y = ocrWord.Y,
                    Width = ocrWord.Width,
                    Height = ocrWord.Height
                };

                result.Words.Add(textWord);
                textLine.Words.Add(textWord);
            }

            result.Lines.Add(textLine);
        }

        return result;
    }
}

#region OCR Result Models

/// <summary>
/// Результат OCR-распознавания
/// </summary>
public class OcrDetectionResult
{
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    public List<OcrWord> Words { get; set; } = new();
    public List<OcrLine> Lines { get; set; } = new();

    public string FullText { get; set; } = "";

    // Оценочные параметры
    public double EstimatedFontSize { get; set; } = 24;
    public double EstimatedLineHeight { get; set; } = 36;
    public double EstimatedLineSpacing { get; set; } = 1.5;
    public double EstimatedPaddingLeft { get; set; }
    public double EstimatedPaddingTop { get; set; }
    public TextLayoutAlignment EstimatedAlignment { get; set; } = TextLayoutAlignment.Left;
}

/// <summary>
/// Слово, распознанное OCR
/// </summary>
public class OcrWord
{
    public int Index { get; set; }
    public int LineIndex { get; set; }
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// Строка, распознанная OCR
/// </summary>
public class OcrLine
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<OcrWord> Words { get; set; } = new();
}

#endregion
