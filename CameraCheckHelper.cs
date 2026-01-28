using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace NeuroBureau.Experiment;

/// <summary>
/// Утилита для проверки камеры.
/// 
/// ВАЖНО: НЕ используйте перебор input_format / pixel_format / video_size!
/// Это вызывает ошибки на разных версиях FFmpeg.
/// 
/// Вместо этого используйте простую проверку: камера в списке = работает.
/// FFmpeg сам договорится с камерой о формате при записи.
/// </summary>
public static class CameraCheckHelper
{
    /// <summary>
    /// Простая проверка камеры: есть в списке dshow = работает.
    /// НЕ пытается открыть камеру с разными форматами.
    /// </summary>
    public static async Task<CameraCheckResult> CheckCameraAsync(string ffmpegExe, string cameraName)
    {
        // 1. Проверяем, есть ли камера в списке устройств
        var devices = await CameraDeviceProvider.GetVideoDevicesAsync(ffmpegExe);

        var deviceExists = devices.Exists(d =>
            string.Equals(d.FriendlyName, cameraName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.AlternativeName, cameraName, StringComparison.OrdinalIgnoreCase));

        if (!deviceExists)
        {
            return new CameraCheckResult(false, $"Камера '{cameraName}' не найдена в списке устройств");
        }

        // 2. Опционально: быстрая проверка доступности (без перебора форматов)
        return await CameraDeviceProvider.CheckCameraSimpleAsync(ffmpegExe, cameraName);
    }

    /// <summary>
    /// Показывает диалог с результатом проверки камеры.
    /// </summary>
    public static async Task<bool> CheckCameraWithDialogAsync(string ffmpegExe, string cameraName, Window? owner = null)
    {
        var result = await CheckCameraAsync(ffmpegExe, cameraName);

        if (result.Success)
        {
            // Успех - не показываем диалог, просто возвращаем true
            return true;
        }

        // Ошибка - показываем диалог с возможностью продолжить
        var msg = $"Проверка камеры завершилась с предупреждением:\n\n" +
                  $"Камера: {cameraName}\n" +
                  $"Статус: {result.Message}\n\n" +
                  $"Возможные причины:\n" +
                  $"• Камера занята другим приложением\n" +
                  $"• Драйвер камеры не установлен\n" +
                  $"• Камера отключена\n\n" +
                  $"Продолжить запуск эксперимента?\n" +
                  $"(Запись камеры может не работать)";

        var dialogResult = MessageBox.Show(
            owner,
            msg,
            "Проверка камеры",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return dialogResult == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Получает информацию о камере (возможности, форматы).
    /// Использовать только для отображения, НЕ для выбора формата при записи.
    /// </summary>
    public static async Task<string> GetCameraInfoAsync(string ffmpegExe, string cameraName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Камера: {cameraName}");
        sb.AppendLine();

        try
        {
            // Получаем список возможностей камеры (только для информации)
            var args = $"-hide_banner -f dshow -list_options true -i video=\"{cameraName}\"";

            var psi = new ProcessStartInfo(ffmpegExe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null)
            {
                sb.AppendLine("Не удалось получить информацию о камере");
                return sb.ToString();
            }

            var stderr = await p.StandardError.ReadToEndAsync();
            p.WaitForExit(5000);

            // Парсим вывод
            var lines = stderr.Split('\n');
            bool inOptions = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.Contains("DirectShow video device options"))
                {
                    inOptions = true;
                    sb.AppendLine("Поддерживаемые форматы:");
                    continue;
                }

                if (inOptions && (trimmed.StartsWith("pixel_format") || trimmed.StartsWith("vcodec")))
                {
                    sb.AppendLine($"  {trimmed}");
                }
            }

            if (!inOptions)
            {
                sb.AppendLine("Информация о форматах недоступна");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Ошибка: {ex.Message}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Проверяет версию FFmpeg и выдаёт рекомендации.
    /// </summary>
    public static async Task<string> GetFfmpegRecommendationAsync(string ffmpegExe)
    {
        var version = await CameraDeviceProvider.GetFfmpegVersionAsync(ffmpegExe);

        if (string.IsNullOrEmpty(version))
        {
            return "Не удалось определить версию FFmpeg.\n" +
                   "Рекомендуется использовать FFmpeg 6.0 или новее.";
        }

        if (!CameraDeviceProvider.IsVersionSupported(version))
        {
            return $"Версия FFmpeg: {version}\n" +
                   $"⚠️ Версия может быть устаревшей.\n" +
                   $"Рекомендуется обновить до FFmpeg 6.0 или новее.";
        }

        return $"Версия FFmpeg: {version} ✓";
    }
}

/* 
============================================================================
ИНСТРУКЦИЯ ПО ИНТЕГРАЦИИ
============================================================================

Если у вас есть окно проверки камеры (CameraCheckWindow или подобное),
которое перебирает разные input_format/pixel_format/video_size:

1. УДАЛИТЕ весь код перебора форматов. Он не нужен и вызывает ошибки
   на разных версиях FFmpeg.

2. ЗАМЕНИТЕ на простую проверку:

   // БЫЛО (плохо):
   foreach (var format in new[] { "mjpeg", "yuyv422", "nv12" })
   {
       foreach (var size in new[] { "1280x720", "640x480" })
       {
           var args = $"-f dshow -input_format {format} -video_size {size} ...";
           // пробуем запустить...
       }
   }

   // СТАЛО (хорошо):
   var result = await CameraCheckHelper.CheckCameraAsync(ffmpegExe, cameraName);
   if (result.Success)
   {
       // Камера работает, продолжаем
   }
   else
   {
       // Показываем ошибку
   }

3. При записи просто используйте FfmpegRecorder.StartCameraAsync() -
   он сам попробует несколько вариантов и выберет рабочий.

============================================================================
*/
