// File: MultiExportViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NeuroBureau.Experiment;

public sealed class MultiExportViewModel : ObservableObject
{
    private readonly HashSet<string> _initialSelectedResultUids;
    private readonly FilenameTemplateResolver _resolver = new();

    public string ExpDir { get; }

    private ExperimentFile? _experiment;
    public ExperimentFile? Experiment
    {
        get => _experiment;
        private set => SetProperty(ref _experiment, value);
    }

    public ObservableCollection<MultiExportStimulusItem> Stimuli { get; } = new();
    public ObservableCollection<MultiExportResultItem> Results { get; } = new();

    public Array Modes { get; } = Enum.GetValues(typeof(MultiExportMode));
    public Array DataFormats { get; } = Enum.GetValues(typeof(DataExportFormat));
    public Array ImageFormats { get; } = Enum.GetValues(typeof(ImageExportFormat));

    private string _outputDir = "";
    public string OutputDir
    {
        get => _outputDir;
        set
        {
            if (SetProperty(ref _outputDir, (value ?? "").Trim()))
                OnPropertyChanged(nameof(CanStartExport));
        }
    }

    private string _filenameTemplate = "%date%_%id_result%_%id_stimul%_%type%";
    public string FilenameTemplate
    {
        get => _filenameTemplate;
        set
        {
            if (SetProperty(ref _filenameTemplate, value ?? ""))
            {
                ValidateTemplate();
                OnPropertyChanged(nameof(CanStartExport));
            }
        }
    }

    private bool _templateIsValid = true;
    public bool TemplateIsValid
    {
        get => _templateIsValid;
        private set
        {
            if (SetProperty(ref _templateIsValid, value))
            {
                OnPropertyChanged(nameof(TemplateStatusEmoji));
                OnPropertyChanged(nameof(CanStartExport));
            }
        }
    }

    private string _templateStatusText = "ОК";
    public string TemplateStatusText
    {
        get => _templateStatusText;
        private set => SetProperty(ref _templateStatusText, value);
    }

    public string TemplateStatusEmoji => TemplateIsValid ? "✅" : "❌";

    private MultiExportMode _mode = MultiExportMode.SeparateFiles;
    public MultiExportMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                ApplyConstraints();
                OnPropertyChanged(nameof(CanExportImages));
                OnPropertyChanged(nameof(CanExportRawOrSource));
                OnPropertyChanged(nameof(CanStartExport));
            }
        }
    }

    private DataExportFormat _dataFormat = DataExportFormat.CSV;
    public DataExportFormat DataFormat
    {
        get => _dataFormat;
        set => SetProperty(ref _dataFormat, value);
    }

    private ImageExportFormat _imageFormat = ImageExportFormat.PNG;
    public ImageExportFormat ImageFormat
    {
        get => _imageFormat;
        set => SetProperty(ref _imageFormat, value);
    }

    private bool _exportSource = true;
    public bool ExportSource { get => _exportSource; set { if (SetProperty(ref _exportSource, value)) OnPropertyChanged(nameof(CanStartExport)); } }

    private bool _exportRaw = true;
    public bool ExportRaw { get => _exportRaw; set { if (SetProperty(ref _exportRaw, value)) OnPropertyChanged(nameof(CanStartExport)); } }

    private bool _exportActions = true;
    public bool ExportActions { get => _exportActions; set { if (SetProperty(ref _exportActions, value)) OnPropertyChanged(nameof(CanStartExport)); } }

    private bool _exportAoi = true;
    public bool ExportAoi { get => _exportAoi; set { if (SetProperty(ref _exportAoi, value)) OnPropertyChanged(nameof(CanStartExport)); } }

    private bool _exportGazeImage;
    public bool ExportGazeImage { get => _exportGazeImage; set { if (SetProperty(ref _exportGazeImage, value)) OnPropertyChanged(nameof(CanStartExport)); } }

    private bool _exportHeatImage;
    public bool ExportHeatImage { get => _exportHeatImage; set { if (SetProperty(ref _exportHeatImage, value)) OnPropertyChanged(nameof(CanStartExport)); } }

    public bool CanExportImages => Mode is MultiExportMode.SeparateFiles or MultiExportMode.FilePerStimul;
    public bool CanExportRawOrSource => Mode != MultiExportMode.AllInOne;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(CanStartExport));
        }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public bool CanStartExport =>
        !IsBusy
        && TemplateIsValid
        && !string.IsNullOrWhiteSpace(OutputDir)
        && Directory.Exists(OutputDir)
        && Stimuli.Any(s => s.IsSelected)
        && Results.Any(r => r.IsSelected)
        && (ExportAoi || ExportActions || ExportRaw || ExportSource || ExportGazeImage || ExportHeatImage);

    public MultiExportViewModel(string expDir, IReadOnlyCollection<string> initialSelectedResultUids)
    {
        ExpDir = expDir;
        _initialSelectedResultUids = new HashSet<string>(initialSelectedResultUids ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync()
    {
        StatusText = "Загрузка…";

        // загрузка настроек
        var cfg = AppConfigManager.LoadMultiExportSettings();
        OutputDir = cfg.OutputDir;
        if (!Directory.Exists(OutputDir))
        {
            try { Directory.CreateDirectory(OutputDir); } catch { }
        }

        FilenameTemplate = cfg.FilenameTemplate;
        Mode = cfg.Mode;

        ExportSource = cfg.ExportSource;
        ExportRaw = cfg.ExportRaw;
        ExportActions = cfg.ExportActions;
        ExportAoi = cfg.ExportAoi;
        ExportGazeImage = cfg.ExportGazeImage;
        ExportHeatImage = cfg.ExportHeatImage;

        DataFormat = cfg.DataFormat;
        ImageFormat = cfg.ImageFormat;

        // exp.json
        var expPath = Path.Combine(ExpDir, "exp.json");
        if (!File.Exists(expPath))
            throw new FileNotFoundException("Не найден exp.json", expPath);

        Experiment = await Task.Run(() =>
        {
            var json = File.ReadAllText(expPath);
            return JsonSerializer.Deserialize<ExperimentFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("Не удалось распарсить exp.json");
        });

        LoadStimuli();
        LoadResults();

        ApplyConstraints();
        ValidateTemplate();

        StatusText = $"Готово: стимулы={Stimuli.Count}, результаты={Results.Count}";
        OnPropertyChanged(nameof(CanStartExport));
    }

    public void ValidateTemplate()
    {
        if (Experiment == null)
        {
            TemplateIsValid = false;
            TemplateStatusText = "exp.json не загружен";
            return;
        }

        if (_resolver.TryValidate(FilenameTemplate, Experiment, out var err))
        {
            TemplateIsValid = true;
            TemplateStatusText = "ОК";
        }
        else
        {
            TemplateIsValid = false;
            TemplateStatusText = err;
        }
    }

    public MultiExportOptions BuildOptions()
    {
        return new MultiExportOptions
        {
            OutputDir = OutputDir,
            FilenameTemplate = FilenameTemplate,
            Mode = Mode,

            ExportSource = ExportSource,
            ExportRaw = ExportRaw,
            ExportActions = ExportActions,
            ExportAoi = ExportAoi,
            ExportGazeImage = ExportGazeImage,
            ExportHeatImage = ExportHeatImage,

            DataFormat = DataFormat,
            ImageFormat = ImageFormat
        };
    }

    public MultiExportSettings ToSettings()
    {
        return new MultiExportSettings
        {
            OutputDir = OutputDir,
            FilenameTemplate = FilenameTemplate,
            Mode = Mode,

            ExportSource = ExportSource,
            ExportRaw = ExportRaw,
            ExportActions = ExportActions,
            ExportAoi = ExportAoi,
            ExportGazeImage = ExportGazeImage,
            ExportHeatImage = ExportHeatImage,

            DataFormat = DataFormat,
            ImageFormat = ImageFormat
        };
    }

    public IReadOnlyList<StimulFile> GetSelectedStimuli()
        => Stimuli.Where(s => s.IsSelected).Select(s => s.Stimulus).ToList();

    public IReadOnlyList<MultiExportResult> GetSelectedResults()
        => Results.Where(r => r.IsSelected).Select(r => new MultiExportResult(r.Uid, r.Result)).ToList();

    public void SetAllStimuliSelected(bool selected)
    {
        foreach (var s in Stimuli) s.IsSelected = selected;
        OnPropertyChanged(nameof(CanStartExport));
    }

    public void SetAllResultsSelected(bool selected)
    {
        foreach (var r in Results) r.IsSelected = selected;
        OnPropertyChanged(nameof(CanStartExport));
    }

    private void ApplyConstraints()
    {
        // картинки — только SeparateFiles / FilePerStimul
        if (!CanExportImages)
        {
            ExportGazeImage = false;
            ExportHeatImage = false;
        }

        // Raw/Source запрещены в AllInOne
        if (!CanExportRawOrSource)
        {
            ExportRaw = false;
            ExportSource = false;
        }
    }

    private void LoadStimuli()
    {
        Stimuli.Clear();

        // фильтр: IMAGE (мягко: по расширению файла)
        foreach (var st in Experiment!.Stimuls ?? new List<StimulFile>())
        {
            // kind=0 — калибровка
            if ((st.Kind ?? 0) == 0) continue;

            var fn = st.Filename ?? "";
            if (!IsImageFile(fn)) continue;

            var item = new MultiExportStimulusItem(st, isSelected: true);
            item.PropertyChanged += (_, __) => OnPropertyChanged(nameof(CanStartExport));
            Stimuli.Add(item);
        }
    }

    private void LoadResults()
    {
        Results.Clear();

        var dir = Path.Combine(ExpDir, "results");
        if (!Directory.Exists(dir)) return;

        foreach (var rdir in Directory.EnumerateDirectories(dir))
        {
            var uid = Path.GetFileName(rdir);
            var rjson = Path.Combine(rdir, "result.json");
            if (!File.Exists(rjson)) continue;

            ResultFile? rf = null;
            try
            {
                rf = JsonSerializer.Deserialize<ResultFile>(File.ReadAllText(rjson), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { }

            if (rf == null) continue;

            bool preselect = _initialSelectedResultUids.Count == 0 || _initialSelectedResultUids.Contains(uid);

            var item = new MultiExportResultItem(uid, rf, preselect);
            item.PropertyChanged += (_, __) => OnPropertyChanged(nameof(CanStartExport));
            Results.Add(item);
        }
    }

    private static bool IsImageFile(string pathOrName)
    {
        var ext = Path.GetExtension(pathOrName).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }
}
