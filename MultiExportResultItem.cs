// File: MultiExportResultItem.cs
using System;

namespace NeuroBureau.Experiment;

public sealed class MultiExportResultItem : ObservableObject
{
    public string Uid { get; }
    public ResultFile Result { get; }

    public long DurationSec => Result.DurationSec;

    public string StartTimeText
    {
        get
        {
            try
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(Result.TimeUnix).ToLocalTime().DateTime;
                return dt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                return "";
            }
        }
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public MultiExportResultItem(string uid, ResultFile result, bool isSelected)
    {
        Uid = uid;
        Result = result;
        _isSelected = isSelected;
    }
}
