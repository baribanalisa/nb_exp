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

    public AnalysisVisualizationSettings Clone()
    {
        return (AnalysisVisualizationSettings)MemberwiseClone();
    }
    
    public void Normalize()
    {
        if (MinRadius < 1) MinRadius = 1;
        if (MaxRadius < MinRadius) MaxRadius = MinRadius;
        if (LineWidth < 0.1) LineWidth = 0.1;
        if (Alpha < 0) Alpha = 0; if (Alpha > 1) Alpha = 1;

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
    }
}