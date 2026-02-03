using System.Text.Json.Serialization;

namespace NeuroBureau.Experiment;

/// <summary>
/// Метод коррекции вертикального дрифта
/// </summary>
public enum DriftCorrectionMethod
{
    /// <summary>Без коррекции</summary>
    None,

    /// <summary>Привязка к ближайшей строке</summary>
    Slice,

    /// <summary>K-means кластеризация по Y</summary>
    Cluster
}

/// <summary>
/// Привязка фиксации к слову/строке текста
/// </summary>
public sealed class FixationTextBinding
{
    /// <summary>Исходная фиксация</summary>
    public Fixation Fixation { get; set; }

    /// <summary>Индекс слова (null = фиксация вне слов)</summary>
    public int? WordIndex { get; set; }

    /// <summary>Индекс строки (null = фиксация вне строк)</summary>
    public int? LineIndex { get; set; }

    /// <summary>Расстояние до ближайшего слова (px)</summary>
    public double DistanceToWord { get; set; }

    /// <summary>Скорректированная Y-координата (после drift correction)</summary>
    public float CorrectedYpx { get; set; }

    /// <summary>Порядковый номер фиксации в последовательности</summary>
    public int SequenceIndex { get; set; }
}

/// <summary>
/// Метрики чтения для слова
/// </summary>
public sealed class WordReadingMetrics
{
    /// <summary>Индекс слова</summary>
    public int WordIndex { get; set; }

    /// <summary>Текст слова</summary>
    public string WordText { get; set; } = "";

    /// <summary>Индекс строки</summary>
    public int LineIndex { get; set; }

    // ===== Базовые метрики =====

    /// <summary>Количество фиксаций на слове</summary>
    public int FixationCount { get; set; }

    /// <summary>Общая длительность всех фиксаций (сек)</summary>
    public double TotalFixationDuration { get; set; }

    // ===== First-pass reading metrics =====

    /// <summary>First Fixation Duration (FFD) - длительность первой фиксации (сек)</summary>
    public double FirstFixationDuration { get; set; }

    /// <summary>Gaze Duration (GD) - сумма фиксаций при первом проходе (сек)</summary>
    public double GazeDuration { get; set; }

    /// <summary>First of Many Duration - длительность первой фиксации, если фиксаций > 1 (сек)</summary>
    public double FirstOfManyDuration { get; set; }

    // ===== Second-pass metrics =====

    /// <summary>Second Pass Duration - время фиксаций при повторных проходах (сек)</summary>
    public double SecondPassDuration { get; set; }

    /// <summary>Go-Past Duration - время от первой фиксации до выхода правее слова (сек)</summary>
    public double GoPastDuration { get; set; }

    // ===== Позиционные метрики =====

    /// <summary>Initial Landing Position - позиция первой фиксации (0-1, относительно слова)</summary>
    public double InitialLandingPosition { get; set; }

    /// <summary>Initial Landing Position (characters) - позиция в символах</summary>
    public double InitialLandingPositionChar { get; set; }

    /// <summary>Количество регрессий В это слово</summary>
    public int NumberOfRegressionsIn { get; set; }

    /// <summary>Количество регрессий ИЗ этого слова</summary>
    public int NumberOfRegressionsOut { get; set; }

    // ===== Флаги =====

    /// <summary>Слово было пропущено (skip)</summary>
    public bool WasSkipped { get; set; }

    /// <summary>Слово было перефиксировано (refixation)</summary>
    public bool WasRefixated { get; set; }
}

/// <summary>
/// Метрики чтения для строки
/// </summary>
public sealed class LineReadingMetrics
{
    /// <summary>Индекс строки</summary>
    public int LineIndex { get; set; }

    /// <summary>Количество фиксаций на строке</summary>
    public int FixationCount { get; set; }

    /// <summary>Общая длительность фиксаций на строке (сек)</summary>
    public double TotalFixationDuration { get; set; }

    /// <summary>Количество слов в строке</summary>
    public int WordCount { get; set; }

    /// <summary>Количество пропущенных слов</summary>
    public int SkippedWordCount { get; set; }

    /// <summary>Время первой фиксации на строке (сек от начала)</summary>
    public double FirstFixationTime { get; set; }

    /// <summary>Время последней фиксации на строке (сек от начала)</summary>
    public double LastFixationTime { get; set; }

    /// <summary>Reading Order Score (1.0 = идеальный порядок слева-направо)</summary>
    public double ReadingOrderScore { get; set; }
}

/// <summary>
/// Метрики саккад для текста
/// </summary>
public sealed class TextSaccadeMetrics
{
    /// <summary>Общее количество саккад</summary>
    public int TotalSaccades { get; set; }

    /// <summary>Прогрессивные саккады (слева-направо)</summary>
    public int ProgressiveSaccades { get; set; }

    /// <summary>Регрессивные саккады (справа-налево)</summary>
    public int RegressiveSaccades { get; set; }

    /// <summary>Sweep саккады (переходы между строками)</summary>
    public int SweepSaccades { get; set; }

    /// <summary>Средняя амплитуда саккад (градусы)</summary>
    public double MeanSaccadeAmplitudeDeg { get; set; }

    /// <summary>Средняя скорость саккад (градусы/сек)</summary>
    public double MeanSaccadeVelocityDegS { get; set; }

    /// <summary>Средняя длина саккад (px)</summary>
    public double MeanSaccadeAmplitudePx { get; set; }
}

/// <summary>
/// Результат коррекции дрифта
/// </summary>
public sealed class DriftCorrectionResult
{
    /// <summary>Скорректированные фиксации</summary>
    public Fixation[] CorrectedFixations { get; set; } = System.Array.Empty<Fixation>();

    /// <summary>Среднее смещение (px)</summary>
    public double Delta { get; set; }

    /// <summary>Надёжность коррекции (0-1)</summary>
    public double Kappa { get; set; }

    /// <summary>Использованный метод</summary>
    public DriftCorrectionMethod Method { get; set; }
}

/// <summary>
/// Полный результат анализа чтения
/// </summary>
public sealed class ReadingAnalysisResult
{
    /// <summary>Метрики по словам</summary>
    public WordReadingMetrics[] WordMetrics { get; set; } = System.Array.Empty<WordReadingMetrics>();

    /// <summary>Метрики по строкам</summary>
    public LineReadingMetrics[] LineMetrics { get; set; } = System.Array.Empty<LineReadingMetrics>();

    /// <summary>Метрики саккад</summary>
    public TextSaccadeMetrics SaccadeMetrics { get; set; } = new();

    /// <summary>Привязки фиксаций к тексту</summary>
    public FixationTextBinding[] Bindings { get; set; } = System.Array.Empty<FixationTextBinding>();

    /// <summary>Результат коррекции дрифта</summary>
    public DriftCorrectionResult? DriftCorrection { get; set; }

    /// <summary>Общее количество фиксаций</summary>
    public int TotalFixations { get; set; }

    /// <summary>Количество фиксаций, привязанных к словам</summary>
    public int FixationsOnWords { get; set; }

    /// <summary>Процент фиксаций на словах</summary>
    [JsonIgnore]
    public double FixationsOnWordsPercent =>
        TotalFixations > 0 ? (double)FixationsOnWords / TotalFixations * 100 : 0;
}

/// <summary>
/// Настройки анализа текста для стимула
/// </summary>
public sealed class TextAnalysisSettings
{
    /// <summary>Включён ли анализ текста для стимула</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Настройки верстки текста</summary>
    public TextLayoutConfig Layout { get; set; } = new();

    /// <summary>Метод коррекции дрифта</summary>
    public DriftCorrectionMethod DriftCorrection { get; set; } = DriftCorrectionMethod.Slice;

    /// <summary>Максимальное расстояние для привязки фиксации к слову (px)</summary>
    public double MaxFixationDistancePx { get; set; } = 50;

    /// <summary>Минимальная длительность фиксации для учёта (сек)</summary>
    public double MinFixationDurationSec { get; set; } = 0.05;

    /// <summary>Максимальная длительность фиксации для учёта (сек)</summary>
    public double MaxFixationDurationSec { get; set; } = 1.5;
}
