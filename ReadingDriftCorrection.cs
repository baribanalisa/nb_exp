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
            DriftCorrectionMethod.Warp => ApplyWarp(fixations, layout),
            DriftCorrectionMethod.Auto => AutoCorrect(fixations, layout),
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

        // Пробуем все методы и выбираем лучший по (kappa, |delta|)
        var results = new[]
        {
            ApplySlice(fixations, layout),
            ApplyCluster(fixations, layout),
            ApplyWarp(fixations, layout)
        };

        // Выбираем метод с лучшим kappa (при равном kappa - меньший delta)
        DriftCorrectionResult best = results[0];
        foreach (var r in results)
        {
            double rKappa = r.Kappa ?? 0;
            double bestKappa = best.Kappa ?? 0;
            if (rKappa > bestKappa ||
                (Math.Abs(rKappa - bestKappa) < 0.01 && Math.Abs(r.Delta) < Math.Abs(best.Delta)))
            {
                best = r;
            }
        }

        return best;
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

    #region Warp Method

    /// <summary>
    /// Warp: Dynamic Time Warping - нелинейная коррекция с учётом прогрессии чтения
    /// Учитывает, что читатель движется слева направо и сверху вниз
    /// </summary>
    private static DriftCorrectionResult ApplyWarp(
        IReadOnlyList<Fixation> fixations,
        TextLayoutResult layout)
    {
        if (layout.Lines.Count == 0)
        {
            return ApplySlice(fixations, layout);
        }

        var corrected = new Fixation[fixations.Count];
        var deltas = new List<double>();

        // Разбиваем фиксации на "проходы" по строкам
        // Проход = последовательность фиксаций до регрессии на предыдущую строку
        var passes = DetectReadingPasses(fixations, layout);

        foreach (var pass in passes)
        {
            // Для каждого прохода определяем, какой строке он соответствует
            int lineIndex = EstimateLineForPass(pass.Fixations, layout, pass.EstimatedLine);

            if (lineIndex >= 0 && lineIndex < layout.Lines.Count)
            {
                var line = layout.Lines[lineIndex];
                foreach (var (fix, idx) in pass.Fixations)
                {
                    float newY = (float)line.CenterY;
                    float delta = newY - fix.Ypx;
                    deltas.Add(delta);
                    corrected[idx] = new Fixation(fix.StartSec, fix.DurSec, fix.Xpx, newY);
                }
            }
            else
            {
                // Fallback: оставляем как есть
                foreach (var (fix, idx) in pass.Fixations)
                {
                    corrected[idx] = fix;
                }
            }
        }

        double avgDelta = deltas.Count > 0 ? deltas.Average() : 0;
        double kappa = CalculateKappa(deltas);

        return new DriftCorrectionResult
        {
            CorrectedFixations = corrected,
            Delta = avgDelta,
            Kappa = kappa,
            Method = DriftCorrectionMethod.Warp
        };
    }

    /// <summary>
    /// Обнаруживает "проходы" чтения - группы последовательных фиксаций на одной строке
    /// </summary>
    private static List<ReadingPass> DetectReadingPasses(
        IReadOnlyList<Fixation> fixations,
        TextLayoutResult layout)
    {
        var passes = new List<ReadingPass>();
        if (fixations.Count == 0) return passes;

        double lineHeight = layout.LineHeight;
        double sweepThreshold = lineHeight * 0.5; // Порог для определения перехода на новую строку

        var currentPass = new ReadingPass();
        int estimatedLine = 0;

        for (int i = 0; i < fixations.Count; i++)
        {
            var fix = fixations[i];

            if (currentPass.Fixations.Count == 0)
            {
                currentPass.Fixations.Add((fix, i));
                currentPass.EstimatedLine = estimatedLine;
                continue;
            }

            var prevFix = currentPass.Fixations[^1].Fix;

            // Проверяем, это sweep (переход на новую строку)?
            bool isSweep = fix.Xpx < prevFix.Xpx - 100 && // Значительный скачок влево
                          Math.Abs(fix.Ypx - prevFix.Ypx) > sweepThreshold; // И вертикальное смещение

            // Проверяем, это регрессия на предыдущую строку?
            bool isRegressionUp = fix.Ypx < prevFix.Ypx - sweepThreshold;

            if (isSweep)
            {
                // Завершаем текущий проход, начинаем новый
                if (currentPass.Fixations.Count > 0)
                {
                    passes.Add(currentPass);
                }
                estimatedLine++;
                currentPass = new ReadingPass { EstimatedLine = estimatedLine };
                currentPass.Fixations.Add((fix, i));
            }
            else if (isRegressionUp)
            {
                // Регрессия - новый проход, но на предыдущей строке
                if (currentPass.Fixations.Count > 0)
                {
                    passes.Add(currentPass);
                }
                estimatedLine = Math.Max(0, estimatedLine - 1);
                currentPass = new ReadingPass { EstimatedLine = estimatedLine };
                currentPass.Fixations.Add((fix, i));
            }
            else
            {
                // Продолжаем текущий проход
                currentPass.Fixations.Add((fix, i));
            }
        }

        // Добавляем последний проход
        if (currentPass.Fixations.Count > 0)
        {
            passes.Add(currentPass);
        }

        return passes;
    }

    /// <summary>
    /// Оценивает, какой строке соответствует проход
    /// </summary>
    private static int EstimateLineForPass(
        List<(Fixation Fix, int Idx)> passFixations,
        TextLayoutResult layout,
        int estimatedLine)
    {
        if (passFixations.Count == 0) return estimatedLine;

        // Используем медианную Y-координату прохода
        var yValues = passFixations.Select(f => (double)f.Fix.Ypx).OrderBy(y => y).ToList();
        double medianY = yValues[yValues.Count / 2];

        // Находим ближайшую строку
        int bestLine = 0;
        double bestDist = double.MaxValue;

        for (int i = 0; i < layout.Lines.Count; i++)
        {
            double dist = Math.Abs(layout.Lines[i].CenterY - medianY);

            // Добавляем штраф за несоответствие ожидаемой строке
            // (учитываем, что чтение идёт сверху вниз)
            double penalty = Math.Abs(i - estimatedLine) * layout.LineHeight * 0.3;

            if (dist + penalty < bestDist)
            {
                bestDist = dist + penalty;
                bestLine = i;
            }
        }

        return bestLine;
    }

    private class ReadingPass
    {
        public List<(Fixation Fix, int Idx)> Fixations { get; } = new();
        public int EstimatedLine { get; set; }
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
