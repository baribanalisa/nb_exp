using System;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace NeuroBureau.Experiment;
public sealed record ExperimentSource(
    string ExpDir,        // где лежит exp.json
    string WorkRootDir,   // корень распаковки (его будем паковать обратно)
    string? ArchivePath   // если выбран tar.gz, то путь к нему, иначе null
);
public static class ExperimentLoader
{
    public static ExperimentSource PrepareExperiment(string selectedPath)
    {
        if (Directory.Exists(selectedPath))
        {
            var expJson = FindExpJson(selectedPath) ?? throw new FileNotFoundException("exp.json не найден в выбранной папке");
            var expDir = Path.GetDirectoryName(expJson)!;
            return new ExperimentSource(expDir, selectedPath, ArchivePath: null);
        }

        if (!File.Exists(selectedPath))
            throw new FileNotFoundException("Выбранный файл не найден", selectedPath);

        if (selectedPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            var baseDir = Path.GetDirectoryName(selectedPath)!;
            var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(selectedPath));
            var workRoot = Path.Combine(baseDir, $"{name}_work_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(workRoot);

            ExtractTarGz(selectedPath, workRoot);

            var expJson = FindExpJson(workRoot) ?? throw new FileNotFoundException("exp.json не найден после распаковки архива");
            var expDir = Path.GetDirectoryName(expJson)!;

            return new ExperimentSource(expDir, workRoot, selectedPath);
        }

        if (Path.GetFileName(selectedPath).Equals("exp.json", StringComparison.OrdinalIgnoreCase))
        {
            var expDir = Path.GetDirectoryName(selectedPath)!;
            return new ExperimentSource(expDir, expDir, ArchivePath: null);
        }

        throw new InvalidOperationException("Выберите .tar.gz, папку или exp.json");
    }
    public static void RepackTarGz(string sourceRootDir, string targetTarGzPath, bool makeBackup = true)
    {
        var tmp = targetTarGzPath + ".tmp";

        using (var outFs = File.Create(tmp))
        using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
        using (var writer = new TarWriter(gz, leaveOpen: false))
        {
            // 1) директории (чтобы структура была красивой; не строго обязательно)
            var dirs = Directory.EnumerateDirectories(sourceRootDir, "*", SearchOption.AllDirectories);
            foreach (var dir in dirs)
            {
                var rel = Path.GetRelativePath(sourceRootDir, dir).Replace('\\', '/');
                if (string.IsNullOrEmpty(rel) || rel == ".") continue;
                if (!rel.EndsWith("/")) rel += "/";

                var dEntry = new PaxTarEntry(TarEntryType.Directory, rel);
                writer.WriteEntry(dEntry);
            }

            // 2) файлы
            var files = Directory.EnumerateFiles(sourceRootDir, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(sourceRootDir, file).Replace('\\', '/');
                if (string.IsNullOrEmpty(rel) || rel == ".") continue;

                using var fs = File.OpenRead(file);
                var fEntry = new PaxTarEntry(TarEntryType.RegularFile, rel)
                {
                    DataStream = fs
                };
                writer.WriteEntry(fEntry);
            }
        }

        if (makeBackup && File.Exists(targetTarGzPath))
        {
            File.Copy(targetTarGzPath, targetTarGzPath + ".bak", overwrite: true);
        }

        File.Move(tmp, targetTarGzPath, overwrite: true);
    }
    private static string? FindExpJson(string rootDir) =>
        Directory.EnumerateFiles(rootDir, "exp.json", SearchOption.AllDirectories).FirstOrDefault();

    private static void ExtractTarGz(string tarGzPath, string destDir)
    {
        // Нужен для Encoding.GetEncoding(866/1251) в .NET
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var fs = File.OpenRead(tarGzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        ExtractTarStreamCompat(gz, destDir);
    }

    private static void ExtractTarStreamCompat(Stream tarStream, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        Directory.CreateDirectory(destFull);

        var header = new byte[512];
        string? gnuLongName = null;
        string? paxPath = null;

        while (true)
        {
            if (!ReadExactly(tarStream, header, 0, 512, allowEof: true))
                break;

            if (IsAllZero(header))
                break;

            byte typeflag = header[156];
            long size = ReadOctal(header, 124, 12);

            string name = DecodeNameField(header, 0, 100);
            string prefix = DecodeNameField(header, 345, 155);

            string entryName = (!string.IsNullOrEmpty(prefix) ? $"{prefix}/{name}" : name);

            // GNU LongName / PAX path overrides
            if (!string.IsNullOrEmpty(gnuLongName))
            {
                entryName = gnuLongName!;
                gnuLongName = null;
            }
            if (!string.IsNullOrEmpty(paxPath))
            {
                entryName = paxPath!;
                paxPath = null;
            }

            entryName = entryName.Replace('\\', '/');
            if (entryName.StartsWith("./")) entryName = entryName.Substring(2);
            entryName = entryName.TrimStart('/');

            // GNU longname entry
            if (typeflag == (byte)'L')
            {
                var data = ReadBytes(tarStream, size);
                gnuLongName = DecodeNameBytes(data).TrimEnd('\0', '\n', '\r');
                SkipPadding(tarStream, size);
                continue;
            }

            // PAX extended header (path=...)
            if (typeflag == (byte)'x' || typeflag == (byte)'g')
            {
                var data = ReadBytes(tarStream, size);
                paxPath = ParsePaxPath(data);
                SkipPadding(tarStream, size);
                continue;
            }

            bool isDir = typeflag == (byte)'5' || entryName.EndsWith("/");

            if (string.IsNullOrWhiteSpace(entryName))
            {
                SkipBytes(tarStream, size);
                SkipPadding(tarStream, size);
                continue;
            }

            var relOs = entryName.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(destFull, relOs));

            // защита от path traversal
            if (!fullPath.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
            {
                SkipBytes(tarStream, size);
                SkipPadding(tarStream, size);
                continue;
            }

            if (isDir)
            {
                Directory.CreateDirectory(fullPath);
                if (size > 0) SkipBytes(tarStream, size);
                SkipPadding(tarStream, size);
                continue;
            }

            // regular file only ('0' or '\0')
            if (typeflag != 0 && typeflag != (byte)'0')
            {
                SkipBytes(tarStream, size);
                SkipPadding(tarStream, size);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using (var outFs = File.Create(fullPath))
            {
                CopyBytes(tarStream, outFs, size);
            }
            SkipPadding(tarStream, size);
        }
    }

    private static bool ReadExactly(Stream s, byte[] buffer, int offset, int count, bool allowEof)
    {
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buffer, offset + total, count - total);
            if (r == 0)
            {
                if (allowEof && total == 0) return false;
                throw new EndOfStreamException("Неожиданный конец потока tar");
            }
            total += r;
        }
        return true;
    }

    private static bool IsAllZero(byte[] buf)
    {
        for (int i = 0; i < buf.Length; i++)
            if (buf[i] != 0) return false;
        return true;
    }

    private static long ReadOctal(byte[] header, int offset, int length)
    {
        var s = Encoding.ASCII.GetString(header, offset, length).Trim('\0', ' ', '\t');
        if (string.IsNullOrEmpty(s)) return 0;
        return Convert.ToInt64(s, 8);
    }

    private static string DecodeNameField(byte[] header, int offset, int length)
    {
        return DecodeNameBytes(new ReadOnlySpan<byte>(header, offset, length));
    }

    private static string DecodeNameBytes(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;

        var data = bytes.Slice(0, end).ToArray();
        if (data.Length == 0) return "";

        // UTF-8 strict
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch { }

        // старые архивы с Windows часто пишут имена в OEM-866
        try { return Encoding.GetEncoding(866).GetString(data); }
        catch { }

        // иногда встречается 1251
        try { return Encoding.GetEncoding(1251).GetString(data); }
        catch { }

        // последний шанс: сохранить байты 1:1
        return Encoding.Latin1.GetString(data);
    }

    private static byte[] ReadBytes(Stream s, long count)
    {
        if (count < 0 || count > int.MaxValue)
            throw new InvalidOperationException("Слишком большой элемент tar");

        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int r = s.Read(buffer, read, (int)count - read);
            if (r == 0) throw new EndOfStreamException("Неожиданный конец потока tar");
            read += r;
        }
        return buffer;
    }

    private static void CopyBytes(Stream src, Stream dst, long count)
    {
        byte[] buf = new byte[81920];
        long left = count;

        while (left > 0)
        {
            int toRead = (int)Math.Min(buf.Length, left);
            int r = src.Read(buf, 0, toRead);
            if (r == 0) throw new EndOfStreamException("Неожиданный конец потока tar");
            dst.Write(buf, 0, r);
            left -= r;
        }
    }

    private static void SkipBytes(Stream s, long count)
    {
        byte[] buf = new byte[81920];
        long left = count;

        while (left > 0)
        {
            int toRead = (int)Math.Min(buf.Length, left);
            int r = s.Read(buf, 0, toRead);
            if (r == 0) throw new EndOfStreamException("Неожиданный конец потока tar");
            left -= r;
        }
    }

    private static void SkipPadding(Stream s, long size)
    {
        long padding = (512 - (size % 512)) % 512;
        if (padding > 0) SkipBytes(s, padding);
    }

    private static string? ParsePaxPath(byte[] data)
    {
        int idx = 0;
        while (idx < data.Length)
        {
            int sp = Array.IndexOf(data, (byte)' ', idx);
            if (sp < 0) break;

            var lenStr = Encoding.ASCII.GetString(data, idx, sp - idx);
            if (!int.TryParse(lenStr, out int len) || len <= 0) break;
            if (idx + len > data.Length) break;

            int recStart = sp + 1;
            int recEnd = idx + len - 1; // '\n'
            int recLen = recEnd - recStart;

            if (recLen > 0)
            {
                var rec = data.AsSpan(recStart, recLen);
                int eq = rec.IndexOf((byte)'=');
                if (eq > 0)
                {
                    var key = Encoding.ASCII.GetString(rec.Slice(0, eq));
                    if (key == "path")
                        return Encoding.UTF8.GetString(rec.Slice(eq + 1));
                }
            }

            idx += len;
        }
        return null;
    }

    public static string GetExperimentsRoot()
    {
        var cfg = TrackerPaths.FindExistingOrDefault("config.json");

        try
        {
            if (File.Exists(cfg))
            {
                var t = JsonSerializer.Deserialize<TrackerConfig>(File.ReadAllText(cfg));
                var raw = t?.ExpPathUnderscore ?? t?.ExpPathDash;

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var p = raw.Trim();
                    if (!Path.IsPathRooted(p))
                        p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));

                    Directory.CreateDirectory(p);
                    return p;
                }
            }
        }
        catch
        {
            // ignore
        }


        // 2) fallback как в Vala по умолчанию: Documents/Experiments
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = Path.Combine(docs, "Experiments");
        Directory.CreateDirectory(root);
        return root;
    }

    public static string ImportTarGzIntoExperiments(string tarGzPath, string experimentsRoot)
    {
        Directory.CreateDirectory(experimentsRoot);

        // распакуем во временную папку
        var tempRoot = Path.Combine(Path.GetTempPath(), "nb_import_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        ExtractTarGz(tarGzPath, tempRoot);

        // архив обычно содержит одну верхнюю папку <exp_uid>/exp.json
        // ищем exp.json и берём папку, где он лежит
        var expJson = Directory.EnumerateFiles(tempRoot, "exp.json", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("exp.json не найден внутри импортированного архива");

        var expDir = Path.GetDirectoryName(expJson)!;

        // имя папки эксперимента = uid (как в старом Neurobureau)
        var uidFolder = Path.GetFileName(expDir);
        if (string.IsNullOrWhiteSpace(uidFolder))
            uidFolder = "import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // не перетирать существующее — создаём новую папку
        var destDir = Path.Combine(experimentsRoot, uidFolder);
        if (Directory.Exists(destDir))
            destDir = Path.Combine(experimentsRoot, uidFolder + "_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        // если exp.json лежит глубже (temp/foo/bar/exp.json), мы переносим именно expDir целиком
        try
        {
            Directory.Move(expDir, destDir);
        }
        catch
        {
            CopyDirectory(expDir, destDir);
        }

        // подчистим temp
        try { Directory.Delete(tempRoot, recursive: true); } catch { /* ignore */ }

        // важно: exp.json должен быть прямо в destDir
        if (!File.Exists(Path.Combine(destDir, "exp.json")))
            throw new InvalidOperationException("В импортированной папке эксперимента нет exp.json в корне (анализатор не увидит).");

        return destDir;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);

        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private sealed class TrackerConfig
    {
        [JsonPropertyName("exp_path")]
        public string? ExpPathUnderscore { get; set; }

        [JsonPropertyName("exp-path")]
        public string? ExpPathDash { get; set; }
    }

}
