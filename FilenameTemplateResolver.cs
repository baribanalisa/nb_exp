// File: FilenameTemplateResolver.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NeuroBureau.Experiment;

public sealed class FilenameTemplateResolver
{
    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "date",
        "id_result",
        "id_stimul",
        "name_result",
        "name_stimul",
        "type",
        "stimul_filename",
    };

    public bool TryValidate(string template, ExperimentFile exp, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(template))
        {
            error = "Шаблон пустой";
            return false;
        }

        // проверка %...%
        int i = 0;
        while (i < template.Length)
        {
            int p = template.IndexOf('%', i);
            if (p < 0) break;

            if (p + 1 < template.Length && template[p + 1] == '%')
            {
                i = p + 2; // literal %
                continue;
            }

            int q = template.IndexOf('%', p + 1);
            if (q < 0)
            {
                error = "Незакрытый макрос: отсутствует завершающий %";
                return false;
            }

            var token = template.Substring(p + 1, q - p - 1).Trim();
            if (token.Length == 0)
            {
                error = "Пустой макрос %%";
                return false;
            }

            if (!IsAllowedToken(token, exp))
            {
                error = $"Неизвестный макрос: %{token}%";
                return false;
            }

            i = q + 1;
        }

        return true;
    }

    public string Resolve(
        string template,
        DateTime now,
        ExperimentFile exp,
        ResultFile result,
        StimulFile stimul,
        string type,
        string? defaultExtensionWithoutDot = null)
    {
        var chars = BuildCharMap(result);

        string ReplaceToken(string token)
        {
            token = token.Trim();

            if (token.Equals("date", StringComparison.OrdinalIgnoreCase))
                return now.ToString("yyyyMMdd_HHmmss");

            if (token.Equals("id_result", StringComparison.OrdinalIgnoreCase))
                return chars.TryGetValue("__id_result", out var v) ? v : "";

            if (token.Equals("id_stimul", StringComparison.OrdinalIgnoreCase))
                return stimul.Uid;

            if (token.Equals("name_result", StringComparison.OrdinalIgnoreCase))
            {
                var name = GetResultName(result);
                return string.IsNullOrWhiteSpace(name)
                    ? (chars.TryGetValue("__id_result", out var id) ? id : "")
                    : name;
            }

            if (token.Equals("name_stimul", StringComparison.OrdinalIgnoreCase))
            {
                var filename = Path.GetFileNameWithoutExtension(stimul.Filename ?? "");
                return string.IsNullOrWhiteSpace(filename) ? stimul.Uid : filename;
            }

            if (token.Equals("type", StringComparison.OrdinalIgnoreCase))
                return type;

            if (token.Equals("stimul_filename", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(stimul.Filename ?? "");

            // иначе — характеристика
            if (chars.TryGetValue(token, out var cv))
                return cv;

            return "";
        }

        var res = new System.Text.StringBuilder(template.Length + 64);

        int i = 0;
        while (i < template.Length)
        {
            int p = template.IndexOf('%', i);
            if (p < 0)
            {
                res.Append(template, i, template.Length - i);
                break;
            }

            res.Append(template, i, p - i);

            if (p + 1 < template.Length && template[p + 1] == '%')
            {
                res.Append('%');
                i = p + 2;
                continue;
            }

            int q = template.IndexOf('%', p + 1);
            if (q < 0)
            {
                // как защита: невалидный шаблон — просто допишем хвост
                res.Append(template.Substring(p));
                break;
            }

            var token = template.Substring(p + 1, q - p - 1);
            res.Append(ReplaceToken(token));

            i = q + 1;
        }

        var name = SanitizeFileName(res.ToString());

        if (!string.IsNullOrWhiteSpace(defaultExtensionWithoutDot))
        {
            var ext = "." + defaultExtensionWithoutDot.Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
                name += ext;
        }

        return name;
    }

    private static bool IsAllowedToken(string token, ExperimentFile exp)
    {
        if (BuiltIn.Contains(token)) return true;

        // характеристика (по exp.Characteristics)
        var defs = exp.Characteristics ?? new List<CharacteristicDef>();
        return defs.Any(d => !string.IsNullOrWhiteSpace(d.Name) &&
                             d.Name!.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> BuildCharMap(ResultFile rf)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in rf.CharsData ?? new List<CharValue>())
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            dict[c.Name.Trim()] = (c.Val ?? "").Trim();
        }

        // зарезервируем для Resolve(id_result)
        dict["__id_result"] = ""; // заполним снаружи при необходимости

        return dict;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "export";

        return cleaned;
    }

    private static string GetResultName(ResultFile result)
    {
        var charsData = result.CharsData;
        if (charsData == null || charsData.Count == 0) return "";

        // Ищем характеристику с именем участника
        var nameChar = charsData.FirstOrDefault(c =>
            c.Name != null && (
                c.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("имя", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("participant", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("участник", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("испытуемый", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals("фио", StringComparison.OrdinalIgnoreCase)));

        return nameChar?.Val ?? "";
    }
}
