// File: MultiExportSettings.cs
using System;
using System.IO;

namespace NeuroBureau.Experiment;

/// <summary>
/// Формат данных для экспорта
/// </summary>
public enum ExportDataFormat
{
    CSV,
    XLSX
}

/// <summary>
/// Формат изображений для экспорта
/// </summary>
public enum ExportImageFormat
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

    /// <summary>
    /// Формат данных для экспорта (CSV или XLSX)
    /// </summary>
    public ExportDataFormat DataFormat { get; set; } = ExportDataFormat.CSV;

    /// <summary>
    /// Формат изображений для экспорта (PNG или JPG)
    /// </summary>
    public ExportImageFormat ImageFormat { get; set; } = ExportImageFormat.PNG;

    public bool ExportSource { get; set; } = false;
    public bool ExportRaw { get; set; } = true;
    public bool ExportActions { get; set; } = true;
    public bool ExportAoi { get; set; } = true;

    // Эти пункты в сервисе включены «мягко»: если заранее сгенерированных файлов нет — пропускаем.
    public bool ExportGazeImage { get; set; } = true;
    public bool ExportHeatImage { get; set; } = true;

    public bool ExportEdf { get; set; } = false;
}
