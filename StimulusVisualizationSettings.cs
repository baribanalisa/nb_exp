namespace NeuroBureau.Experiment;

public enum StimulusVisualizationMode
{
    GazePath = 0,
    Heatmap = 1,
    BeeSwarm = 2,
    AreasOfInterest = 3,
    TextAnalysis = 4,
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
    public TextAnalysisSettings TextAnalysis { get; set; } = new();

    public StimulusVizSettings Clone()
    {
        return new StimulusVizSettings
        {
            Mode = Mode,
            Heatmap = Heatmap.Clone(),
            TextAnalysis = new TextAnalysisSettings
            {
                IsEnabled = TextAnalysis.IsEnabled,
                Layout = new TextLayoutConfig
                {
                    Text = TextAnalysis.Layout.Text,
                    FontName = TextAnalysis.Layout.FontName,
                    FontSizePx = TextAnalysis.Layout.FontSizePx,
                    LineSpacing = TextAnalysis.Layout.LineSpacing,
                    MaxWidthPx = TextAnalysis.Layout.MaxWidthPx,
                    PaddingLeft = TextAnalysis.Layout.PaddingLeft,
                    PaddingTop = TextAnalysis.Layout.PaddingTop,
                    Alignment = TextAnalysis.Layout.Alignment
                },
                DriftCorrection = TextAnalysis.DriftCorrection,
                MaxFixationDistancePx = TextAnalysis.MaxFixationDistancePx,
                MinFixationDurationSec = TextAnalysis.MinFixationDurationSec,
                MaxFixationDurationSec = TextAnalysis.MaxFixationDurationSec
            }
        };
    }

    public void Normalize()
    {
        Heatmap.Normalize();
    }
}