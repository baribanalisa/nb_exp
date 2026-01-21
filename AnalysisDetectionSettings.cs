using System;

namespace NeuroBureau.Experiment;

public enum FixationAlgorithm
{
    Idt,
    Ivt,
}

public enum JoinFixType
{
    DontJoinFix,
    JoinFixByTime,
    JoinFixByTimeAngle,
}

public enum NoiseReductionType
{
    None,
    MovingAverage,
    Median,
}

public enum EyeSelection
{
    AverageEye,
    LeftEye,
    RightEye,
}

/// <summary>
/// Параметры детекции фиксаций/скорости в анализе.
/// По смыслу повторяет конфиг из Vala (Speed.Config), плюс IDT.
/// </summary>
public sealed class AnalysisDetectionSettings
{
    public FixationAlgorithm Algorithm { get; set; } = FixationAlgorithm.Idt;

    // ===== Общие (как в Vala) =====
    public NoiseReductionType NoiseReduction { get; set; } = NoiseReductionType.None;

    /// <summary>Окно фильтра (в сэмплах), для MovingAverage/Median.</summary>
    public int WindowSize { get; set; } = 5;

    /// <summary>Макс. длина разрыва (в сэмплах), который интерполируем.</summary>
    public int GapWindowSize { get; set; } = 3;

    public EyeSelection Eye { get; set; } = EyeSelection.AverageEye;

    /// <summary>Верхняя граница шкалы скорости (только визуализация, на расчёт не влияет).</summary>
    public double MaxSpeedDegPerSec { get; set; } = 600;

    // ===== I-DT =====
    public double IdtDispersionThresholdPx { get; set; } = 60;

    /// <summary>Минимальная длительность фиксации, мс.</summary>
    public int IdtMinDurationMs { get; set; } = 80;

    /// <summary>Минимальное стартовое окно, мс (MinWindowDuration).</summary>
    public int IdtWindowMs { get; set; } = 80;

    /// <summary>Объединение фиксаций по времени, мс.</summary>
    public int IdtMergeTimeMs { get; set; } = 0;

    // ===== I-VT =====
    /// <summary>Порог скорости (°/с), ниже которого считаем фиксацию.</summary>
    public double IvtSpeedFixDegPerSec { get; set; } = 30;

    /// <summary>Минимальная длительность фиксации, мс.</summary>
    public int IvtMinDurationMs { get; set; } = 80;

    public JoinFixType IvtJoinType { get; set; } = JoinFixType.JoinFixByTime;

    public int IvtMergeTimeMs { get; set; } = 75;

    public double IvtMergeAngleDeg { get; set; } = 30;

    public AnalysisDetectionSettings Clone()
    {
        return (AnalysisDetectionSettings)MemberwiseClone();
    }
}
