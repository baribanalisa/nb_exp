namespace NeuroBureau.Experiment;

public sealed class AnalysisVisualizationSettings
{
    // Существующие настройки
    public double MinRadius { get; set; } = 14;
    public double MaxRadius { get; set; } = 46;
    public double MaxDurationSec { get; set; } = 1.2;
    public double LineWidth { get; set; } = 2.0;
    public double Alpha { get; set; } = 0.75;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 16;

    // === Новые настройки для Bee Swarm ===
    public double BeeRadius { get; set; } = 20.0;
    public double BeeLineWidth { get; set; } = 3.0;

    // === Настройки для тепловой карты ===
    public HeatmapFalloff HeatmapFunction { get; set; } = HeatmapFalloff.Exponential;
    public double HeatmapRadius { get; set; } = 42.0;
    public double HeatmapInitialOpacity { get; set; } = 0.65;
    public double HeatmapThreshold { get; set; } = 0.05;
    public HeatmapType HeatmapMapType { get; set; } = HeatmapType.Heatmap;

    // === КГР: фильтрация графиков (SR/SC/HR/PPG) ===
    /// <summary>Включить фильтрацию КГР-графиков в анализе (только отображение).</summary>
    public bool KgrFilterEnabled { get; set; } = true;

    /// <summary>Медианный фильтр (устранение одиночных выбросов).</summary>
    public bool KgrUseMedianFilter { get; set; } = true;

    /// <summary>Ширина окна медианного фильтра, секунды (переводится во внутреннее окно в сэмплах).</summary>
    public double KgrMedianWindowSec { get; set; } = 0.35;

    /// <summary>EMA (экспоненциальное сглаживание).</summary>
    public bool KgrUseEmaFilter { get; set; } = true;

    public double KgrSrEmaTauSec { get; set; } = 1.2;
    public double KgrScEmaTauSec { get; set; } = 1.0;
    public double KgrHrEmaTauSec { get; set; } = 2.0;
    public double KgrPpgEmaTauSec { get; set; } = 0.12;

    /// <summary>Ограничение HR по диапазону и максимальному скачку (slew rate).</summary>
    public bool KgrClampHr { get; set; } = true;

    public double KgrHrMin { get; set; } = 30;
    public double KgrHrMax { get; set; } = 220;

    /// <summary>Макс. изменение HR в секунду (bpm/sec). 0 — отключить ограничение скачка.</summary>
    public double KgrHrMaxDeltaPerSec { get; set; } = 35;

    public AnalysisVisualizationSettings Clone()
    {
        return (AnalysisVisualizationSettings)MemberwiseClone();
    }

    public void Normalize()
    {
        if (MinRadius < 1) MinRadius = 1;
        if (MaxRadius < MinRadius) MaxRadius = MinRadius;
        if (LineWidth < 0.1) LineWidth = 0.1;
        if (Alpha < 0) Alpha = 0;
        if (Alpha > 1) Alpha = 1;

        // Валидация Bee Swarm
        if (BeeRadius < 2) BeeRadius = 2;
        if (BeeLineWidth < 1) BeeLineWidth = 1;

        // Валидация Heatmap
        if (HeatmapRadius < 1) HeatmapRadius = 1;
        if (HeatmapRadius > 2000) HeatmapRadius = 2000;
        if (HeatmapInitialOpacity < 0) HeatmapInitialOpacity = 0;
        if (HeatmapInitialOpacity > 1) HeatmapInitialOpacity = 1;
        if (HeatmapThreshold < 0) HeatmapThreshold = 0;
        if (HeatmapThreshold > 1) HeatmapThreshold = 1;

        // Валидация КГР
        if (KgrMedianWindowSec < 0) KgrMedianWindowSec = 0;
        if (KgrMedianWindowSec > 60) KgrMedianWindowSec = 60;

        if (KgrSrEmaTauSec < 0) KgrSrEmaTauSec = 0;
        if (KgrScEmaTauSec < 0) KgrScEmaTauSec = 0;
        if (KgrHrEmaTauSec < 0) KgrHrEmaTauSec = 0;
        if (KgrPpgEmaTauSec < 0) KgrPpgEmaTauSec = 0;

        if (KgrSrEmaTauSec > 600) KgrSrEmaTauSec = 600;
        if (KgrScEmaTauSec > 600) KgrScEmaTauSec = 600;
        if (KgrHrEmaTauSec > 600) KgrHrEmaTauSec = 600;
        if (KgrPpgEmaTauSec > 600) KgrPpgEmaTauSec = 600;

        if (KgrHrMin < 0) KgrHrMin = 0;
        if (KgrHrMax < 0) KgrHrMax = 0;

        if (KgrHrMax < KgrHrMin)
            KgrHrMax = KgrHrMin;

        if (KgrHrMaxDeltaPerSec < 0) KgrHrMaxDeltaPerSec = 0;
        if (KgrHrMaxDeltaPerSec > 1000) KgrHrMaxDeltaPerSec = 1000;
    }
}
