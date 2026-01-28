namespace NeuroBureau.Experiment;

internal sealed record CameraDeviceInfo(string FriendlyName, string? AlternativeName)
{
    public override string ToString() => FriendlyName;
}