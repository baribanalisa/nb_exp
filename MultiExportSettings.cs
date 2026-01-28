// File: MultiExportSettings.cs
using System;
using System.IO;

namespace NeuroBureau.Experiment;

public enum DataExportFormat
{
    CSV,
    XLSX
}

public enum ImageExportFormat
{
    PNG,
    JPG
}

public sealed class MultiExportSettings
{
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "multi-export");

    public string FilenameTemplate { get; set; } = "%date%_%id_result%_%id_stimul%_%type%";

    public MultiExportMode Mode { get; set; } = MultiExportMode.SeparateFiles;

    public bool ExportSource { get; set; } = true;
    public bool ExportRaw { get; set; } = true;
    public bool ExportActions { get; set; } = true;
    public bool ExportAoi { get; set; } = true;

    // Эти пункты в сервисе включены «мягко»: если заранее сгенерированных файлов нет — пропускаем.
    public bool ExportGazeImage { get; set; } = false;
    public bool ExportHeatImage { get; set; } = false;

    // Форматы экспорта
    public DataExportFormat DataFormat { get; set; } = DataExportFormat.CSV;
    public ImageExportFormat ImageFormat { get; set; } = ImageExportFormat.PNG;
}
