using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows;

namespace NeuroBureau.Experiment;

/// <summary>
/// Выравнивание текста
/// </summary>
public enum TextLayoutAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// Настройки верстки текста для анализа чтения
/// </summary>
public sealed class TextLayoutConfig
{
    /// <summary>Текст для анализа</summary>
    public string Text { get; set; } = "";

    /// <summary>Название шрифта</summary>
    public string FontName { get; set; } = "Segoe UI";

    /// <summary>Размер шрифта в пикселях</summary>
    public double FontSizePx { get; set; } = 24;

    /// <summary>Межстрочный интервал (множитель)</summary>
    public double LineSpacing { get; set; } = 1.5;

    /// <summary>Максимальная ширина текста (для переноса строк)</summary>
    public double MaxWidthPx { get; set; } = 800;

    /// <summary>Отступ слева</summary>
    public double PaddingLeft { get; set; } = 50;

    /// <summary>Отступ сверху</summary>
    public double PaddingTop { get; set; } = 50;

    /// <summary>Выравнивание текста</summary>
    public TextLayoutAlignment Alignment { get; set; } = TextLayoutAlignment.Left;
}

/// <summary>
/// Слово с координатами в пространстве стимула
/// </summary>
public sealed class TextWord
{
    /// <summary>Порядковый номер слова (0-based)</summary>
    public int Index { get; set; }

    /// <summary>Индекс строки, в которой находится слово</summary>
    public int LineIndex { get; set; }

    /// <summary>Текст слова</summary>
    public string Text { get; set; } = "";

    /// <summary>Левый край (px)</summary>
    public double X { get; set; }

    /// <summary>Верхний край (px)</summary>
    public double Y { get; set; }

    /// <summary>Ширина слова (px)</summary>
    public double Width { get; set; }

    /// <summary>Высота слова (px)</summary>
    public double Height { get; set; }

    /// <summary>Позиция начала слова в исходном тексте</summary>
    public int CharStart { get; set; }

    /// <summary>Позиция конца слова в исходном тексте</summary>
    public int CharEnd { get; set; }

    /// <summary>Центр слова по X</summary>
    [JsonIgnore]
    public double CenterX => X + Width / 2;

    /// <summary>Центр слова по Y</summary>
    [JsonIgnore]
    public double CenterY => Y + Height / 2;

    /// <summary>Bounding box слова</summary>
    [JsonIgnore]
    public Rect Bounds => new Rect(X, Y, Width, Height);
}

/// <summary>
/// Строка текста
/// </summary>
public sealed class TextLine
{
    /// <summary>Индекс строки (0-based)</summary>
    public int Index { get; set; }

    /// <summary>Координата Y верхнего края строки</summary>
    public double Y { get; set; }

    /// <summary>Высота строки</summary>
    public double Height { get; set; }

    /// <summary>Слова в этой строке</summary>
    public List<TextWord> Words { get; set; } = new();

    /// <summary>Исходный текст строки</summary>
    public string Text { get; set; } = "";

    /// <summary>Левый край строки (X первого слова)</summary>
    [JsonIgnore]
    public double X => Words.Count > 0 ? Words[0].X : 0;

    /// <summary>Ширина строки (от первого до последнего слова)</summary>
    [JsonIgnore]
    public double Width => Words.Count > 0
        ? Words[^1].X + Words[^1].Width - Words[0].X
        : 0;

    /// <summary>Центр строки по Y</summary>
    [JsonIgnore]
    public double CenterY => Y + Height / 2;

    /// <summary>Bounding box строки</summary>
    [JsonIgnore]
    public Rect Bounds => new Rect(X, Y, Width, Height);
}

/// <summary>
/// Результат верстки текста
/// </summary>
public sealed class TextLayoutResult
{
    /// <summary>Исходная конфигурация</summary>
    public TextLayoutConfig Config { get; set; } = new();

    /// <summary>Список строк</summary>
    public List<TextLine> Lines { get; set; } = new();

    /// <summary>Плоский список всех слов</summary>
    public List<TextWord> Words { get; set; } = new();

    /// <summary>Общая ширина контента</summary>
    public double ContentWidth { get; set; }

    /// <summary>Общая высота контента</summary>
    public double ContentHeight { get; set; }

    /// <summary>Высота одной строки (с учетом line spacing)</summary>
    public double LineHeight { get; set; }

    /// <summary>Проверяет, содержит ли результат данные</summary>
    [JsonIgnore]
    public bool IsEmpty => Words.Count == 0;
}
