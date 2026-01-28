// File: MultiExportOptions.cs
namespace NeuroBureau.Experiment;

public sealed class MultiExportOptions
{
    public string OutputDir { get; init; } = "";
    public string FilenameTemplate { get; init; } = "%date%_%id_result%_%id_stimul%_%type%";
    public MultiExportMode Mode { get; init; } = MultiExportMode.SeparateFiles;

    public bool ExportSource { get; init; }
    public bool ExportRaw { get; init; }
    public bool ExportActions { get; init; }
    public bool ExportAoi { get; init; }
    public bool ExportGazeImage { get; init; }
    public bool ExportHeatImage { get; init; }

    // Форматы экспорта
    public DataExportFormat DataFormat { get; init; } = DataExportFormat.CSV;
    public ImageExportFormat ImageFormat { get; init; } = ImageExportFormat.PNG;
}
