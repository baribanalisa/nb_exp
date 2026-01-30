// File: ImageGenerator.cs
// Класс для headless генерации изображений карт взгляда и тепловых карт
// Использует WPF в STA потоке для рендеринга

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NeuroBureau.Experiment;

/// <summary>
/// Генератор изображений для мультиэкспорта (headless режим)
/// </summary>
public static class ImageGenerator
{
    /// <summary>
    /// Генерирует карту движения взгляда (gaze path)
    /// </summary>
    public static WriteableBitmap? GenerateGazeMap(
        List<Fixation> fixations,
        int width,
        int height,
        AnalysisVisualizationSettings settings,
        System.Drawing.Color color,
        string? backgroundPath = null)
    {
        if (fixations == null || fixations.Count == 0) return null;
        if (width <= 0 || height <= 0) return null;

        WriteableBitmap? result = null;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                // Создаем FixationOverlay
                var overlay = new FixationOverlay
                {
                    Width = width,
                    Height = height
                };

                // Применяем настройки
                overlay.ApplySettings(settings);

                // Устанавливаем данные
                var series = new List<FixationSeries>
                {
                    new(fixations, System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B))
                };
                overlay.SetFixationSeries(series);

                var root = WrapWithBackground(overlay, width, height, backgroundPath);

                // Рендерим в битмап
                var renderBitmap = new RenderTargetBitmap(
                    width, height, 96, 96, PixelFormats.Pbgra32);
                
                renderBitmap.Render(root);

                // Конвертируем в WriteableBitmap
                result = new WriteableBitmap(renderBitmap);
                result.Freeze(); // Делаем thread-safe
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
            throw new InvalidOperationException($"Ошибка генерации карты движения: {error.Message}", error);

        return result;
    }

    /// <summary>
    /// Генерирует тепловую карту
    /// </summary>
    public static WriteableBitmap? GenerateHeatmap(
        List<HeatmapSample> samples,
        int width,
        int height,
        StimulusHeatmapSettings settings,
        string? backgroundPath = null)
    {
        if (samples == null || samples.Count == 0) return null;
        if (width <= 0 || height <= 0) return null;

        WriteableBitmap? result = null;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                // Создаем HeatmapOverlay
                var overlay = new HeatmapOverlay
                {
                    Width = width,
                    Height = height
                };

                // Применяем настройки
                overlay.ApplySettings(settings);

                // Устанавливаем данные
                overlay.SetSamples(samples);

                var root = WrapWithBackground(overlay, width, height, backgroundPath);

                // Рендерим в битмап
                var renderBitmap = new RenderTargetBitmap(
                    width, height, 96, 96, PixelFormats.Pbgra32);
                
                renderBitmap.Render(root);

                // Конвертируем в WriteableBitmap
                result = new WriteableBitmap(renderBitmap);
                result.Freeze(); // Делаем thread-safe
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
            throw new InvalidOperationException($"Ошибка генерации тепловой карты: {error.Message}", error);

        return result;
    }

    /// <summary>
    /// Генерирует композитную карту движения для нескольких результатов
    /// </summary>
    public static WriteableBitmap? GenerateCompositeGazeMap(
        List<(List<Fixation> Fixations, System.Windows.Media.Color Color)> seriesData,
        int width,
        int height,
        AnalysisVisualizationSettings settings,
        string? backgroundPath = null)
    {
        if (seriesData == null || seriesData.Count == 0) return null;
        if (width <= 0 || height <= 0) return null;

        WriteableBitmap? result = null;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var overlay = new FixationOverlay
                {
                    Width = width,
                    Height = height
                };

                overlay.ApplySettings(settings);

                var series = seriesData
                    .Where(s => s.Fixations != null && s.Fixations.Count > 0)
                    .Select(s => new FixationSeries(s.Fixations, s.Color))
                    .ToList();

                if (series.Count == 0) return;

                overlay.SetFixationSeries(series);

                var root = WrapWithBackground(overlay, width, height, backgroundPath);

                var renderBitmap = new RenderTargetBitmap(
                    width, height, 96, 96, PixelFormats.Pbgra32);
                
                renderBitmap.Render(root);

                result = new WriteableBitmap(renderBitmap);
                result.Freeze();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
            throw new InvalidOperationException($"Ошибка генерации композитной карты: {error.Message}", error);

        return result;
    }

    /// <summary>
    /// Генерирует композитную тепловую карту для нескольких результатов
    /// </summary>
    public static WriteableBitmap? GenerateCompositeHeatmap(
        List<(List<HeatmapSample> Samples, System.Windows.Media.Color Color)> seriesData,
        int width,
        int height,
        StimulusHeatmapSettings settings,
        string? backgroundPath = null)
    {
        if (seriesData == null || seriesData.Count == 0) return null;
        if (width <= 0 || height <= 0) return null;

        WriteableBitmap? result = null;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var overlay = new HeatmapOverlay
                {
                    Width = width,
                    Height = height
                };

                overlay.ApplySettings(settings);

                var series = seriesData
                    .Where(s => s.Samples != null && s.Samples.Count > 0)
                    .Select(s => new HeatmapSeries(s.Samples, s.Color))
                    .ToList();

                if (series.Count == 0) return;

                overlay.SetHeatmapSeries(series);

                var root = WrapWithBackground(overlay, width, height, backgroundPath);

                var renderBitmap = new RenderTargetBitmap(
                    width, height, 96, 96, PixelFormats.Pbgra32);
                
                renderBitmap.Render(root);

                result = new WriteableBitmap(renderBitmap);
                result.Freeze();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
            throw new InvalidOperationException($"Ошибка генерации композитной тепловой карты: {error.Message}", error);

        return result;
    }

    private static FrameworkElement WrapWithBackground(FrameworkElement overlay, int width, int height, string? backgroundPath)
    {
        overlay.Width = width;
        overlay.Height = height;

        if (string.IsNullOrWhiteSpace(backgroundPath) || !File.Exists(backgroundPath))
        {
            overlay.Measure(new System.Windows.Size(width, height));
            overlay.Arrange(new System.Windows.Rect(0, 0, width, height));
            return overlay;
        }

        var bitmap = LoadBitmapImage(backgroundPath);
        if (bitmap == null)
        {
            overlay.Measure(new System.Windows.Size(width, height));
            overlay.Arrange(new System.Windows.Rect(0, 0, width, height));
            return overlay;
        }

        var grid = new Grid
        {
            Width = width,
            Height = height
        };

        var image = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill
        };

        grid.Children.Add(image);
        grid.Children.Add(overlay);

        grid.Measure(new System.Windows.Size(width, height));
        grid.Arrange(new System.Windows.Rect(0, 0, width, height));

        return grid;
    }

    private static BitmapImage? LoadBitmapImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
