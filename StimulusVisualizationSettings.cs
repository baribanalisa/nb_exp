namespace NeuroBureau.Experiment;

public enum StimulusVisualizationMode
{
    GazePath = 0,
    Heatmap = 1,
    BeeSwarm = 2,
    AreasOfInterest = 3,
}

public enum HeatmapFalloff
{
    Constant = 0,
    Linear = 1,
    Exponential = 2,
}

public enum HeatmapType
{
    Heatmap = 0,
    Fog = 1,
}

public sealed class StimulusHeatmapSettings
{
    public HeatmapFalloff Function { get; set; } = HeatmapFalloff.Exponential;
    public double Radius { get; set; } = 42;
    public double InitialOpacity { get; set; } = 0.65;
    public double Threshold { get; set; } = 0.05;
    public HeatmapType MapType { get; set; } = HeatmapType.Heatmap;

    public StimulusHeatmapSettings Clone()
    {
        return (StimulusHeatmapSettings)MemberwiseClone();
    }

    public void Normalize()
    {
        if (Radius < 1) Radius = 1;
        if (Radius > 2000) Radius = 2000;
        if (InitialOpacity < 0) InitialOpacity = 0;
        if (InitialOpacity > 1) InitialOpacity = 1;
        if (Threshold < 0) Threshold = 0;
        if (Threshold > 1) Threshold = 1;
    }
}

public sealed class StimulusVizSettings
{
    public StimulusVisualizationMode Mode { get; set; } = StimulusVisualizationMode.GazePath;
    public StimulusHeatmapSettings Heatmap { get; set; } = new();

    public StimulusVizSettings Clone()
    {
        return new StimulusVizSettings
        {
            Mode = Mode,
            Heatmap = Heatmap.Clone(),
        };
    }

    public void Normalize()
    {
        Heatmap.Normalize();
    }
}