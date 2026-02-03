using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NeuroBureau.Experiment;

/// <summary>
/// Загрузчик текстовых стимулов из CSV или JSON файлов.
/// Поддерживает формат, совместимый с Python Eyekit.
/// </summary>
public static class TextStimuliLoader
{
    /// <summary>
    /// Ищет и загружает конфигурацию текста для стимула.
    /// Порядок поиска:
    /// 1. {expDir}/stimuli.csv - CSV с параметрами текста
    /// 2. {expDir}/stimuli.json - JSON с сериализованными TextBlock
    /// 3. {resultDir}/{stimUid}/text_layout.json - сохранённые настройки
    /// </summary>
    public static TextLayoutConfig? LoadForStimulus(string expDir, string stimUid, string? resultUid = null)
    {
        // 1. Попробуем загрузить из stimuli.csv
        var csvPath = Path.Combine(expDir, "stimuli.csv");
        if (File.Exists(csvPath))
        {
            var config = LoadFromCsv(csvPath, stimUid);
            if (config != null) return config;
        }

        // 2. Попробуем загрузить из stimuli.json
        var jsonPath = Path.Combine(expDir, "stimuli.json");
        if (File.Exists(jsonPath))
        {
            var config = LoadFromJson(jsonPath, stimUid);
            if (config != null) return config;
        }

        // 3. Попробуем загрузить сохранённые настройки
        if (!string.IsNullOrWhiteSpace(resultUid))
        {
            var savedPath = Path.Combine(expDir, "results", resultUid, stimUid, "text_layout.json");
            if (File.Exists(savedPath))
            {
                return LoadSavedConfig(savedPath);
            }
        }

        return null;
    }

    /// <summary>
    /// Загружает конфигурацию текста из CSV файла.
    /// Формат CSV (разделитель ; или ,):
    /// id;text;font_name;font_size;line_height;margin_left;margin_top;align;max_width
    /// </summary>
    public static TextLayoutConfig? LoadFromCsv(string csvPath, string stimUid)
    {
        try
        {
            var lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2) return null;

            // Определяем разделитель
            char delimiter = lines[0].Contains(';') ? ';' : ',';

            // Парсим заголовок
            var headers = lines[0].Split(delimiter)
                .Select(h => h.Trim().ToLowerInvariant().Replace("\"", ""))
                .ToArray();

            var idIdx = FindColumnIndex(headers, "id", "stimulus_id", "stim_id", "uid");
            var textIdx = FindColumnIndex(headers, "text", "content", "stimulus_text");
            var fontNameIdx = FindColumnIndex(headers, "font_name", "font_face", "font", "fontname");
            var fontSizeIdx = FindColumnIndex(headers, "font_size", "fontsize", "size");
            var lineHeightIdx = FindColumnIndex(headers, "line_height", "lineheight", "line_spacing");
            var marginLeftIdx = FindColumnIndex(headers, "margin_left", "left", "ml", "padding_left", "geom_pos_x", "x");
            var marginTopIdx = FindColumnIndex(headers, "margin_top", "top", "mt", "padding_top", "geom_pos_y", "y");
            var alignIdx = FindColumnIndex(headers, "align", "alignment", "text_align");
            var maxWidthIdx = FindColumnIndex(headers, "max_width", "maxwidth", "wrap_width", "wrapwidth", "width");
            var screenWidthIdx = FindColumnIndex(headers, "screen_width", "screenwidth", "screen_w");
            var screenHeightIdx = FindColumnIndex(headers, "screen_height", "screenheight", "screen_h");

            if (idIdx < 0 || textIdx < 0) return null;

            // Ищем строку с нужным id
            for (int i = 1; i < lines.Length; i++)
            {
                var values = ParseCsvLine(lines[i], delimiter);
                if (values.Length <= Math.Max(idIdx, textIdx)) continue;

                var id = values[idIdx].Trim().Replace("\"", "");

                // Сравниваем id (может быть с расширением или без)
                if (!string.Equals(id, stimUid, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFileNameWithoutExtension(id), Path.GetFileNameWithoutExtension(stimUid), StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = values[textIdx].Trim().Replace("\"", "");

                // Нормализуем переносы строк (как в Python)
                text = text.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n");

                if (string.IsNullOrWhiteSpace(text)) continue;

                var config = new TextLayoutConfig
                {
                    Text = text,
                    FontName = GetValue(values, fontNameIdx, "Times New Roman"),
                    FontSizePx = GetDouble(values, fontSizeIdx, 24),
                    LineSpacing = GetDouble(values, lineHeightIdx, 0) > 0
                        ? GetDouble(values, lineHeightIdx, 24) / GetDouble(values, fontSizeIdx, 24)
                        : 1.5,
                    PaddingLeft = GetDouble(values, marginLeftIdx, 100),
                    PaddingTop = GetDouble(values, marginTopIdx, 100),
                    Alignment = ParseAlignment(GetValue(values, alignIdx, "left")),
                    MaxWidthPx = GetDouble(values, maxWidthIdx, 0)
                };

                // Если max_width не задан, но есть screen_width, используем его
                if (config.MaxWidthPx <= 0 && screenWidthIdx >= 0)
                {
                    var screenW = GetDouble(values, screenWidthIdx, 1920);
                    config.MaxWidthPx = screenW - config.PaddingLeft * 2;
                }

                return config;
            }
        }
        catch
        {
            // Игнорируем ошибки парсинга
        }

        return null;
    }

    /// <summary>
    /// Загружает конфигурацию текста из JSON файла (формат Eyekit TextBlock)
    /// </summary>
    public static TextLayoutConfig? LoadFromJson(string jsonPath, string stimUid)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Ищем запись по id
            JsonElement? found = null;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var id = GetJsonString(item, "id") ?? GetJsonString(item, "stimulus_id");
                    if (string.Equals(id, stimUid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(id ?? ""),
                                      Path.GetFileNameWithoutExtension(stimUid), StringComparison.OrdinalIgnoreCase))
                    {
                        found = item;
                        break;
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Попробуем найти по ключу
                if (root.TryGetProperty(stimUid, out var item))
                {
                    found = item;
                }
                else
                {
                    // Ищем по вложенному id
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            var id = GetJsonString(prop.Value, "id");
                            if (string.Equals(id, stimUid, StringComparison.OrdinalIgnoreCase))
                            {
                                found = prop.Value;
                                break;
                            }
                        }
                    }
                }
            }

            if (found == null) return null;
            var elem = found.Value;

            // Проверяем, есть ли TextBlock
            JsonElement textBlock = elem;
            if (elem.TryGetProperty("text_block", out var tb) ||
                elem.TryGetProperty("textblock", out tb) ||
                elem.TryGetProperty("__TextBlock__", out tb))
            {
                textBlock = tb;
            }

            // Извлекаем текст
            string? text = null;
            if (textBlock.TryGetProperty("_text", out var textProp))
            {
                // Eyekit формат: _text может быть строкой или массивом строк
                if (textProp.ValueKind == JsonValueKind.String)
                {
                    text = textProp.GetString();
                }
                else if (textProp.ValueKind == JsonValueKind.Array)
                {
                    text = string.Join("\n", textProp.EnumerateArray().Select(x => x.GetString()));
                }
            }
            else
            {
                text = GetJsonString(textBlock, "text") ?? GetJsonString(elem, "text");
            }

            if (string.IsNullOrWhiteSpace(text)) return null;

            // Извлекаем параметры
            var config = new TextLayoutConfig
            {
                Text = text,
                FontName = GetJsonString(textBlock, "_font_face") ??
                          GetJsonString(textBlock, "font_face") ??
                          GetJsonString(textBlock, "font_name") ??
                          GetJsonString(elem, "font_name") ?? "Times New Roman",
                FontSizePx = GetJsonDouble(textBlock, "_font_size") ??
                            GetJsonDouble(textBlock, "font_size") ??
                            GetJsonDouble(elem, "font_size") ?? 24,
                LineSpacing = (GetJsonDouble(textBlock, "_line_height") ??
                              GetJsonDouble(textBlock, "line_height") ?? 0) > 0
                    ? (GetJsonDouble(textBlock, "_line_height") ?? GetJsonDouble(textBlock, "line_height") ?? 24) /
                      (GetJsonDouble(textBlock, "_font_size") ?? GetJsonDouble(textBlock, "font_size") ?? 24)
                    : 1.5,
                Alignment = ParseAlignment(
                    GetJsonString(textBlock, "_align") ??
                    GetJsonString(textBlock, "align") ??
                    GetJsonString(elem, "align") ?? "left")
            };

            // Позиция
            if (textBlock.TryGetProperty("_position", out var pos) && pos.ValueKind == JsonValueKind.Array)
            {
                var posArr = pos.EnumerateArray().ToArray();
                if (posArr.Length >= 2)
                {
                    config.PaddingLeft = posArr[0].GetDouble();
                    config.PaddingTop = posArr[1].GetDouble();
                }
            }
            else
            {
                config.PaddingLeft = GetJsonDouble(elem, "margin_left") ??
                                    GetJsonDouble(elem, "left") ?? 100;
                config.PaddingTop = GetJsonDouble(elem, "margin_top") ??
                                   GetJsonDouble(elem, "top") ?? 100;
            }

            return config;
        }
        catch
        {
            // Игнорируем ошибки парсинга
        }

        return null;
    }

    /// <summary>
    /// Загружает сохранённую конфигурацию из JSON файла
    /// </summary>
    public static TextLayoutConfig? LoadSavedConfig(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TextLayoutConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Сохраняет конфигурацию в JSON файл
    /// </summary>
    public static void SaveConfig(string path, TextLayoutConfig config)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    #region Helper Methods

    private static int FindColumnIndex(string[] headers, params string[] names)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            foreach (var name in names)
            {
                if (headers[i] == name) return i;
            }
        }
        return -1;
    }

    private static string[] ParseCsvLine(string line, char delimiter)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());

        return result.ToArray();
    }

    private static string GetValue(string[] values, int index, string defaultValue)
    {
        if (index < 0 || index >= values.Length) return defaultValue;
        var v = values[index].Trim().Replace("\"", "");
        return string.IsNullOrWhiteSpace(v) ? defaultValue : v;
    }

    private static double GetDouble(string[] values, int index, double defaultValue)
    {
        if (index < 0 || index >= values.Length) return defaultValue;
        var v = values[index].Trim().Replace("\"", "");
        if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return defaultValue;
    }

    private static string? GetJsonString(JsonElement elem, string prop)
    {
        if (elem.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    private static double? GetJsonDouble(JsonElement elem, string prop)
    {
        if (elem.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number)
                return val.GetDouble();
            if (val.ValueKind == JsonValueKind.String &&
                double.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return null;
    }

    private static TextLayoutAlignment ParseAlignment(string align)
    {
        return align.ToLowerInvariant().Trim() switch
        {
            "center" => TextLayoutAlignment.Center,
            "right" => TextLayoutAlignment.Right,
            _ => TextLayoutAlignment.Left
        };
    }

    #endregion
}
