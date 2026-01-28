// File: MultiExportOptions.cs
namespace NeuroBureau.Experiment;

public sealed class MultiExportOptions
{
    public string OutputDir { get; set; } = "";
    public string FilenameTemplate { get; set; } = "%date%_%id_result%_%id_stimul%_%type%";
    public MultiExportMode Mode { get; set; } = MultiExportMode.SeparateFiles;
    
    /// <summary>
    /// Формат данных для экспорта (CSV или XLSX)
    /// </summary>
    public ExportDataFormat DataFormat { get; set; } = ExportDataFormat.CSV;

    /// <summary>
    /// Формат изображений для экспорта (PNG или JPG)
    /// </summary>
    public ExportImageFormat ImageFormat { get; set; } = ExportImageFormat.PNG;

    public bool ExportSource { get; set; }
    public bool ExportRaw { get; set; }
    public bool ExportActions { get; set; }
    public bool ExportAoi { get; set; }
    public bool ExportGazeImage { get; set; }
    public bool ExportHeatImage { get; set; }
    public bool ExportEdf { get; set; }
}
