using System;
using System.Collections.Generic;
using System.Linq;

namespace NeuroBureau.Experiment;

/// <summary>
/// Коррекция вертикального дрифта фиксаций при чтении.
/// Исправляет систематическое смещение координаты Y.
/// </summary>
public static class ReadingDriftCorrection
{
    /// <summary>
    /// Применяет коррекцию дрифта к фиксациям
    /// </summary>
    /// <param name="fixations">Исходные фиксации</param>
    /// <param name="layout">Верстка текста</param>
    /// <param name="method">Метод коррекции</param>
    /// <returns>Результат коррекции</returns>
    public static DriftCorrectionResult CorrectDrift(
        IReadOnlyList<Fixation> fixations,
        TextLayoutResult layout,
        DriftCorrectionMethod method)
    {
        if (fixations.Count == 0 || layout.IsEmpty)
        {
            return new DriftCorrectionResult
            {
                CorrectedFixations = fixations.ToArray(),
                Method = method
            };
        }

        return method switch
        {
            DriftCorrectionMethod.Slice => ApplySlice(fixations, layout),
            DriftCorrectionMethod.Cluster => ApplyCluster(fixations, layout),
            _ => new DriftCorrectionResult
            {
                CorrectedFixations = fixations.ToArray(),
                Method = DriftCorrectionMethod.None
            }
        };
    }

    /// <summary>
    /// Автоматически выбирает лучший метод коррекции
    /// </summary>
    public static DriftCorrectionResult AutoCorrect(
        IReadOnlyList<Fixation> fixations,
        TextLayoutResult layout)
    {
        if (fixations.Count == 0 || layout.IsEmpty)
        {
            return new DriftCorrectionResult
            {
                CorrectedFixations = fixations.ToArray(),
                Method = DriftCorrectionMethod.None
            };
        }

        // Пробуем оба метода и выбираем лучший по kappa
        var sliceResult = ApplySlice(fixations, layout);
        var clusterResult = ApplyCluster(fixations, layout);

        // Выбираем метод с лучшим kappa (надёжностью)
        if (clusterResult.Kappa > sliceResult.Kappa)
            return clusterResult;

        return sliceResult;
    }

    #region Slice Method

    /// <summary>
    /// Slice: привязка каждой фиксации к ближайшей строке
    /// </summary>
    private static DriftCorrectionResult ApplySlice(
        IReadOnlyList<Fixation> fixations,
        TextLayoutResult layout)
    {
        var corrected = new Fixation[fixations.Count];
        var deltas = new List<double>();

        for (int i = 0; i < fixations.Count; i++)
        {
            var fix = fixations[i];

            // Находим ближайшую строку
            TextLine? nearestLine = null;
            double minDist = double.MaxValue;

            foreach (var line in layout.Lines)
            {
                double dist = Math.Abs(fix.Ypx - line.CenterY);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearestLine = line;
                }
            }

            if (nearestLine != null)
            {
                // Смещаем Y к центру строки
                float newY = (float)nearestLine.CenterY;
                float delta = newY - fix.Ypx;
                deltas.Add(delta);

                corrected[i] = new Fixation(fix.StartSec, fix.DurSec, fix.Xpx, newY);
            }
            else
            {
                corrected[i] = fix;
            }
        }

        // Вычисляем среднее смещение и надёжность
        double avgDelta = deltas.Count > 0 ? deltas.Average() : 0;
        double kappa = CalculateKappa(deltas);

        return new DriftCorrectionResult
        {
            CorrectedFixations = corrected,
            Delta = avgDelta,
            Kappa = kappa,
            Method = DriftCorrectionMethod.Slice
        };
    }

    #endregion

    #region Cluster Method

    /// <summary>
    /// Cluster: K-means кластеризация по Y, затем привязка кластеров к строкам
    /// </summary>
    private static DriftCorrectionResult ApplyCluster(
        IReadOnlyList<Fixation> fixations,
        TextLayoutResult layout)
    {
        int k = layout.Lines.Count;
        if (k == 0 || fixations.Count < k)
        {
            return ApplySlice(fixations, layout); // Fallback to slice
        }

        // Извлекаем Y-координаты
        var yValues = fixations.Select(f => (double)f.Ypx).ToArray();

        // K-means кластеризация
        var (assignments, centers) = KMeansClustering(yValues, k, maxIterations: 20);

        // Сопоставляем кластеры со строками
        var lineCenters = layout.Lines.Select(l => l.CenterY).ToArray();
        var clusterToLine = MatchClustersToLines(centers, lineCenters);

        // Применяем коррекцию
        var corrected = new Fixation[fixations.Count];
        var deltas = new List<double>();

        for (int i = 0; i < fixations.Count; i++)
        {
            var fix = fixations[i];
            int cluster = assignments[i];

            if (clusterToLine.TryGetValue(cluster, out int lineIdx) && lineIdx < layout.Lines.Count)
            {
                float newY = (float)layout.Lines[lineIdx].CenterY;
                float delta = newY - fix.Ypx;
                deltas.Add(delta);
                corrected[i] = new Fixation(fix.StartSec, fix.DurSec, fix.Xpx, newY);
            }
            else
            {
                corrected[i] = fix;
            }
        }

        double avgDelta = deltas.Count > 0 ? deltas.Average() : 0;
        double kappa = CalculateKappa(deltas);

        return new DriftCorrectionResult
        {
            CorrectedFixations = corrected,
            Delta = avgDelta,
            Kappa = kappa,
            Method = DriftCorrectionMethod.Cluster
        };
    }

    /// <summary>
    /// Простая K-means кластеризация для 1D данных
    /// </summary>
    private static (int[] assignments, double[] centers) KMeansClustering(
        double[] values, int k, int maxIterations = 20)
    {
        int n = values.Length;
        var assignments = new int[n];
        var centers = new double[k];

        // Инициализация: равномерно распределённые центры
        double minY = values.Min();
        double maxY = values.Max();
        double step = (maxY - minY) / (k + 1);

        for (int i = 0; i < k; i++)
        {
            centers[i] = minY + step * (i + 1);
        }

        for (int iter = 0; iter < maxIterations; iter++)
        {
            // Assignment step: каждую точку к ближайшему центру
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                int bestCluster = 0;
                double bestDist = Math.Abs(values[i] - centers[0]);

                for (int c = 1; c < k; c++)
                {
                    double dist = Math.Abs(values[i] - centers[c]);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCluster = c;
                    }
                }

                if (assignments[i] != bestCluster)
                {
                    assignments[i] = bestCluster;
                    changed = true;
                }
            }

            if (!changed) break;

            // Update step: пересчитываем центры
            var counts = new int[k];
            var sums = new double[k];

            for (int i = 0; i < n; i++)
            {
                int c = assignments[i];
                sums[c] += values[i];
                counts[c]++;
            }

            for (int c = 0; c < k; c++)
            {
                if (counts[c] > 0)
                {
                    centers[c] = sums[c] / counts[c];
                }
            }
        }

        return (assignments, centers);
    }

    /// <summary>
    /// Сопоставляет кластеры со строками по ближайшему расстоянию
    /// </summary>
    private static Dictionary<int, int> MatchClustersToLines(double[] clusterCenters, double[] lineCenters)
    {
        var result = new Dictionary<int, int>();
        var usedLines = new HashSet<int>();

        // Сортируем кластеры и строки по Y
        var sortedClusters = clusterCenters
            .Select((y, i) => (y, i))
            .OrderBy(x => x.y)
            .ToList();

        var sortedLines = lineCenters
            .Select((y, i) => (y, i))
            .OrderBy(x => x.y)
            .ToList();

        // Жадное сопоставление по порядку
        int lineIdx = 0;
        foreach (var (cy, ci) in sortedClusters)
        {
            if (lineIdx < sortedLines.Count)
            {
                result[ci] = sortedLines[lineIdx].i;
                lineIdx++;
            }
        }

        return result;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Вычисляет kappa - меру надёжности коррекции (0-1)
    /// Основано на согласованности смещений
    /// </summary>
    private static double CalculateKappa(List<double> deltas)
    {
        if (deltas.Count < 2)
            return 1.0;

        double mean = deltas.Average();
        double variance = deltas.Sum(d => (d - mean) * (d - mean)) / deltas.Count;
        double stdDev = Math.Sqrt(variance);

        // Нормализуем: чем меньше разброс, тем выше kappa
        // Используем сигмоидную функцию
        double maxExpectedStd = 50.0; // ожидаемый максимальный разброс в px
        double normalized = stdDev / maxExpectedStd;

        return Math.Max(0, 1.0 - normalized);
    }

    #endregion
}
