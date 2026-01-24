// File: MultiExportStimulusItem.cs
namespace NeuroBureau.Experiment;

public sealed class MultiExportStimulusItem : ObservableObject
{
    public StimulFile Stimulus { get; }

    public string Uid => Stimulus.Uid;
    public string Filename => Stimulus.Filename ?? "";

    public string DisplayName => $"{Uid} — {Filename}";

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public MultiExportStimulusItem(StimulFile stimulus, bool isSelected)
    {
        Stimulus = stimulus;
        _isSelected = isSelected;
    }
}
