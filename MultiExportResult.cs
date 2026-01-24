// File: MultiExportResult.cs
namespace NeuroBureau.Experiment;

public sealed class MultiExportResult
{
    public string Uid { get; }
    public ResultFile File { get; }

    public MultiExportResult(string uid, ResultFile file)
    {
        Uid = uid;
        File = file;
    }
}
