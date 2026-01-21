
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Псевдонимы
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;

namespace NeuroBureau.Experiment;

public readonly record struct HeatmapSeries(IReadOnlyList<HeatmapSample> Samples, Color Color);

public sealed class HeatmapOverlay : FrameworkElement
{
    private IReadOnlyList<HeatmapSeries>? _series;
    private StimulusHeatmapSettings _settings = new();
    private WriteableBitmap? _bitmap;
    private byte[]? _pixels;
    private double[,]? _mask;
    private int _maskRadius;
    private HeatmapFalloff _maskFunction;
    private double[,]? _heatBuffer;
    private uint[]? _colorTable;

    public void ApplySettings(StimulusHeatmapSettings settings)
    {
        if (settings == null) return;
        _settings = settings.Clone();
        InvalidateMask();
        RenderHeatmap();
    }

    public void SetSamples(IReadOnlyList<HeatmapSample>? samples)
    {
        if (samples == null || samples.Count == 0)
        {
            SetHeatmapSeries(null);
            return;
        }

        // Цвет здесь не важен, так как мы будем использовать палитру
        SetHeatmapSeries(new List<HeatmapSeries> { new(samples, Colors.Red) });
    }

    public void SetHeatmapSeries(IReadOnlyList<HeatmapSeries>? series)
    {
        _series = series;
        RenderHeatmap();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap == null) return;
        dc.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RenderHeatmap();
    }

    private void InvalidateMask()
    {
        _mask = null;
        _colorTable = null;
        _heatBuffer = null;
    }

    // Вставьте этот код ВМЕСТО старого метода RenderHeatmap в HeatmapOverlay.cs

    private void RenderHeatmap()
    {
        int width = (int)Math.Round(ActualWidth > 0 ? ActualWidth : Width);
        int height = (int)Math.Round(ActualHeight > 0 ? ActualHeight : Height);

        if (width <= 0 || height <= 0)
        {
            _bitmap = null;
            return;
        }

        int stride = width * 4;
        int totalBytes = height * stride;

        _pixels ??= new byte[totalBytes];
        if (_pixels.Length != totalBytes) _pixels = new byte[totalBytes];
        
        // Очищаем пиксели (прозрачность)
        Array.Clear(_pixels, 0, _pixels.Length);

        if (_series == null || _series.Count == 0 || _series.All(s => s.Samples.Count == 0))
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixels, stride, 0);
            InvalidateVisual();
            return;
        }

        EnsureMask();
        if (_mask == null) return;

        EnsureHeatBuffer(width, height);
        if (_heatBuffer == null) return;

        int radius = _maskRadius;
        int heatType = GetHeatmapType(_settings); 
        // Если heatType придет 0 или 1 (ч/б), будет серое. 
        // Для радуги убедитесь, что в настройках выбирается тип 2.
        // Или принудительно поставьте: int heatType = 2; 

        // 1. ОЧИЩАЕМ БУФЕР ТЕПЛА В НОЛЬ (было заполнение baseOpacity)
        Array.Clear(_heatBuffer, 0, _heatBuffer.Length);

        // Интенсивность одной точки. Берем из настроек.
        // Если в настройках 0.5, то здесь берем как есть. 
        // (Деление на 20 или 50 лучше делать в AnalysisWindow при подготовке данных, 
        //  но можно и здесь добавить множитель, например * 0.1)
        double intensity = 1.0; // вклад каждой точки = 1; прозрачность управляется InitialOpacity в ApplyPaletteToPixels

        // Локальная функция добавления тепла
        void AddHeat(int cx, int cy)
        {
            for (int y = 0; y < radius * 2; y++)
            {
                int ry = cy - radius + y;
                if (ry < 0 || ry >= height) continue;

                for (int x = 0; x < radius * 2; x++)
                {
                    int rx = cx - radius + x;
                    if (rx < 0 || rx >= width) continue;

                    // 2. СКЛАДЫВАЕМ ТЕПЛО (было вычитание)
                    // mask[y,x] - это форма кисти (0..1). Умножаем на интенсивность.
                    _heatBuffer![ry, rx] += _mask[y, x] * intensity;
                }
            }
        }

        // Проходим по всем точкам и "греем" буфер
        foreach (var series in _series)
        {
            if (series.Samples.Count == 0) continue;
            for (int i = 0; i < series.Samples.Count; i++)
            {
                // Здесь координаты уже должны быть экранными
                AddHeat((int)Math.Round(series.Samples[i].Xpx), (int)Math.Round(series.Samples[i].Ypx));
            }
        }

        // 3. Преобразуем буфер тепла в цвета
        ApplyPaletteToPixels(_heatBuffer, width, height, heatType);

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixels, stride, 0);
        InvalidateVisual();
    }

    // Добавьте/замените этот метод в HeatmapOverlay.cs
    private void ApplyPaletteToPixels(double[,] heat, int width, int height, int heatType)
    {
        if (_pixels == null) return;

        double threshold = Math.Clamp(_settings.Threshold, 0.0, 1.0);

        // alpha — это просто прозрачность слоя (как в старой программе)
        double baseAlpha = Math.Clamp(_settings.InitialOpacity, 0.0, 1.0);
        byte a0 = (byte)Math.Round(255.0 * baseAlpha);

        // Проверяем тип карты через рефлексию
        bool isFogMap = false;
        var settingsType = _settings.GetType();
        var mapTypeProp = settingsType.GetProperty("MapType");
        if (mapTypeProp != null && mapTypeProp.PropertyType == typeof(HeatmapType))
        {
            var mapTypeValue = mapTypeProp.GetValue(_settings);
            if (mapTypeValue is HeatmapType mapType && mapType == HeatmapType.Fog)
            {
                isFogMap = true;
            }
        }

        // Если всё ещё кажется блекло — попробуй 0.85 или 0.75
        const double gamma = 1.0;

        int idx = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double raw = heat[y, x];

                // Для туманной карты рисуем тёмный слой везде
                if (isFogMap)
                {
                    // v - это уровень внимания (0..1)
                    double vFog = raw;
                    if (vFog > 1.0) vFog = 1.0;
                    vFog = Math.Pow(vFog, gamma);

                    // Туманная карта: тёмный слой, где внимание низкое
                    // alpha = baseAlpha * (1 - attention) - чем меньше внимания, тем темнее
                    double fogAlpha = baseAlpha * (1.0 - vFog);

                    if (fogAlpha < 0.01)
                    {
                        idx += 4;
                        continue;
                    }

                    byte aFog = (byte)Math.Round(255.0 * fogAlpha);
                    // Чёрный цвет с varying alpha
                    uint packedFog = PackPremultiplied(0, 0, 0, aFog);

                    _pixels[idx]     = (byte)(packedFog & 0xFF);
                    _pixels[idx + 1] = (byte)((packedFog >> 8) & 0xFF);
                    _pixels[idx + 2] = (byte)((packedFog >> 16) & 0xFF);
                    _pixels[idx + 3] = (byte)((packedFog >> 24) & 0xFF);

                    idx += 4;
                    continue;
                }

                // Обычная тепловая карта
                if (raw <= 0.0)
                {
                    idx += 4;
                    continue;
                }

                // Без нормализации на maxHeat: каждая точка даёт полный градиент,
                // а перекрытия просто “насыщают” до 1.
                double v = raw;
                if (v > 1.0) v = 1.0;

                v = Math.Pow(v, gamma);

                if (v < threshold)
                {
                    idx += 4;
                    continue;
                }

                uint packed;

                if (heatType == 0)
                {
                    byte g = (byte)Math.Round(v * 255.0);
                    packed = PackPremultiplied(g, g, g, a0);
                }
                else if (heatType == 1)
                {
                    byte a = (byte)Math.Round(a0 * v);
                    packed = PackPremultiplied(0, 0, 0, a);
                }
                else
                {
                    var color = GetHeatColor(v, heatType);
                    packed = PackPremultiplied(color.R, color.G, color.B, a0);
                }

                _pixels[idx]     = (byte)(packed & 0xFF);
                _pixels[idx + 1] = (byte)((packed >> 8) & 0xFF);
                _pixels[idx + 2] = (byte)((packed >> 16) & 0xFF);
                _pixels[idx + 3] = (byte)((packed >> 24) & 0xFF);

                idx += 4;
            }
        }
    }

 
    private void EnsureColorTable(int heatType)
    {
        if (_colorTable != null) return;
        byte initAlpha = (byte)Math.Round(Math.Clamp(_settings.InitialOpacity, 0.0, 1.0) * 255);
        _colorTable = new uint[256];
        for (int i = 0; i < _colorTable.Length; i++)
        {
            double val = (double)i / (_colorTable.Length - 1);
            _colorTable[i] = CreatePixel(val, heatType, initAlpha);
        }
    }

    private static uint CreatePixel(double val, int heatType, byte initAlpha)
    {
        val = Math.Clamp(val, 0.0, 1.0);

        if (heatType == 0)
        {
            byte a = (byte)Math.Round(255 * val);
            return PackPremultiplied(a, a, a, a);
        }

        if (heatType == 1)
        {
            byte a = (byte)Math.Round(255 * val);
            return PackPremultiplied(0, 0, 0, a);
        }

        if (val >= 1.0)
        {
            return 0;
        }

        var color = GetHeatColor(1.0 - val, heatType);
        return PackPremultiplied(color.R, color.G, color.B, initAlpha);
    }

    private static uint PackPremultiplied(byte r, byte g, byte b, byte a)
    {
        double alpha = a / 255.0;
        byte pr = (byte)Math.Round(r * alpha);
        byte pg = (byte)Math.Round(g * alpha);
        byte pb = (byte)Math.Round(b * alpha);
        return (uint)(pb | (pg << 8) | (pr << 16) | (a << 24));
    }

    private static (byte R, byte G, byte B) GetHeatColor(double value, int type)
    {
        value = Math.Clamp(value, 0.0, 1.0);

        ReadOnlySpan<(double R, double G, double B)> palette = type switch
        {
            0 => HeatColors1,
            1 => HeatColors2,
            2 => HeatColors3,
            3 => HeatColors4,
            4 => HeatColors5,
            _ => HeatColors6,
        };

        double delta = 1.0 / (palette.Length - 1);
        int index = (int)(value / delta);
        if (index == palette.Length - 1) index--;
        double fract = (value - delta * index) / delta;
        if (fract > 1) fract = 1;

        double r = (palette[index + 1].R - palette[index].R) * fract + palette[index].R;
        double g = (palette[index + 1].G - palette[index].G) * fract + palette[index].G;
        double b = (palette[index + 1].B - palette[index].B) * fract + palette[index].B;

        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static readonly (double R, double G, double B)[] HeatColors1 =
    {
        (1, 1, 1),
        (0, 0, 0),
    };

    private static readonly (double R, double G, double B)[] HeatColors2 =
    {
        (0, 0, 0),
        (1, 1, 1),
    };

    private static readonly (double R, double G, double B)[] HeatColors3 =
    {
        (0, 0, 1),
        (0, 1, 1),
        (0, 1, 0),
        (1, 1, 0),
        (1, 0, 0),
    };

    private static readonly (double R, double G, double B)[] HeatColors4 =
    {
        (0, 0, 1),
        (0, 1, 0),
        (1, 1, 0),
        (1, 0, 0),
    };

    private static readonly (double R, double G, double B)[] HeatColors5 =
    {
        (0, 0, 1),
        (1, 0, 0),
    };

    private static readonly (double R, double G, double B)[] HeatColors6 =
    {
        (1, 0, 0),
        (0, 1, 0),
        (1, 0, 0),
    };

    private void EnsureHeatBuffer(int width, int height)
    {
        if (_heatBuffer != null && _heatBuffer.GetLength(0) == height && _heatBuffer.GetLength(1) == width)
        {
            return;
        }

        _heatBuffer = new double[height, width];
    }

    private static int GetHeatmapType(StimulusHeatmapSettings settings)
    {
        var type = settings.GetType();
        var prop = type.GetProperty("HeatmapType") ?? type.GetProperty("HeatType");
        if (prop?.PropertyType == typeof(int))
        {
            return (int)(prop.GetValue(settings) ?? 2);
        }

        return 2;
    }

    private static double GetMaxOpacity(StimulusHeatmapSettings settings)
    {
        var type = settings.GetType();
        var prop = type.GetProperty("MaxOpacity") ?? type.GetProperty("MaxAlpha");
        if (prop?.PropertyType == typeof(double))
        {
            return (double)(prop.GetValue(settings) ?? settings.InitialOpacity);
        }

        return settings.InitialOpacity;
    }

    private void EnsureMask()
    {
        int radius = Math.Max(1, (int)Math.Round(_settings.Radius));
        radius = Math.Min(radius, 2000);

        var func = _settings.Function;

        if (_mask != null && _maskRadius == radius && _maskFunction == func)
            return;

        _maskRadius = radius;
        _maskFunction = func;
        _colorTable = null;

        int size = radius * 2;
        _mask = new double[size, size];

        // ВАЖНО: opacity (alpha) НЕ должна менять “температуру”.
        // Маска всегда 0..1, а прозрачность регулируем ТОЛЬКО при покраске пикселей.
        for (int y = 0; y < radius; y++)
        {
            for (int x = 0; x < radius; x++)
            {
                double r = Math.Sqrt(x * x + y * y);
                double a;

                if (func == HeatmapFalloff.Constant)
                    a = r < radius ? 1.0 : 0;
                else if (func == HeatmapFalloff.Linear)
                    a = r < radius ? 1.0 - r / radius : 0;
                else
                {
                    double r2 = r / radius * 3.0;
                    a = Math.Exp(-(r2 * r2) / 2.0);
                    if (a < 3.0 / 255.0) a = 0;
                }

                if (a < 0) a = 0;
                if (a > 1.0) a = 1.0;

                int yp = radius + y;
                int yn = radius - y - 1;
                int xp = radius + x;
                int xn = radius - x - 1;

                _mask[yp, xp] = a;
                _mask[yp, xn] = a;
                _mask[yn, xp] = a;
                _mask[yn, xn] = a;
            }
        }
    }

}