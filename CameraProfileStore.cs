using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NeuroBureau.Experiment;

internal sealed record CameraProfileSnapshot(CameraProfile Profile, DateTime LastTestUtc, bool LastTestOk);

internal sealed class CameraProfileStore
{
    private sealed class CameraProfilesFile
    {
        public List<CameraProfileRecord> Profiles { get; set; } = new();
    }

    private sealed class CameraProfileRecord
    {
        public string DeviceName { get; set; } = "";
        public string? VideoSize { get; set; }
        public int Fps { get; set; } = 30;
        public DateTime LastTestUtc { get; set; } = DateTime.UtcNow;
        public bool LastTestOk { get; set; } = true;

        public CameraProfile ToProfile() => new(DeviceName, VideoSize, Fps);
    }

    private readonly string _path;

    public CameraProfileStore(string path) => _path = path;

    public static CameraProfileStore OpenDefault()
        => new(TrackerPaths.FindExistingOrDefault("camera_profiles.json"));

    public CameraProfileSnapshot? Load(string deviceName)
    {
        var file = LoadFile();
        var entry = file.Profiles.FirstOrDefault(p =>
            string.Equals(p.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

        return entry == null
            ? null
            : new CameraProfileSnapshot(entry.ToProfile(), entry.LastTestUtc, entry.LastTestOk);
    }

    public void Save(CameraProfile profile, bool lastTestOk)
    {
        var file = LoadFile();
        var entry = file.Profiles.FirstOrDefault(p =>
            string.Equals(p.DeviceName, profile.DeviceName, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            entry = new CameraProfileRecord();
            file.Profiles.Add(entry);
        }

        entry.DeviceName = profile.DeviceName;
        entry.VideoSize = profile.VideoSize;
        entry.Fps = profile.Fps;
        entry.LastTestOk = lastTestOk;
        entry.LastTestUtc = DateTime.UtcNow;

        SaveFile(file);
    }

    private CameraProfilesFile LoadFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                var file = JsonSerializer.Deserialize<CameraProfilesFile>(
                    File.ReadAllText(_path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (file != null) return file;
            }
        }
        catch
        {
            // ignore
        }

        return new CameraProfilesFile();
    }

    private void SaveFile(CameraProfilesFile file)
    {
        TrackerPaths.EnsureParentDir(_path);

        File.WriteAllText(_path, JsonSerializer.Serialize(file, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}