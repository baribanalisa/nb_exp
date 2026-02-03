using System;
using System.Collections.Generic;
using System.Linq;

namespace NeuroBureau.Experiment;

/// <summary>
/// Калькулятор метрик чтения: привязка фиксаций к тексту и расчёт reading-метрик
/// </summary>
public static class ReadingMetricsCalculator
{
    /// <summary>
    /// Привязывает фиксации к словам и строкам текста
    /// </summary>
    /// <param name="fixations">Список фиксаций</param>
    /// <param name="layout">Результат верстки текста</param>
    /// <param name="maxDistancePx">Максимальное расстояние для привязки к слову (px)</param>
    /// <returns>Список привязок</returns>
    public static List<FixationTextBinding> BindFixationsToText(
        IReadOnlyList<Fixation> fixations,
        TextLayoutResult layout,
        double maxDistancePx = 50)
    {
        var bindings = new List<FixationTextBinding>(fixations.Count);

        for (int i = 0; i < fixations.Count; i++)
        {
            var fix = fixations[i];
            var binding = new FixationTextBinding
            {
                Fixation = fix,
                CorrectedYpx = fix.Ypx,
                SequenceIndex = i
            };

            // Найти ближайшее слово
            double minDist = double.MaxValue;
            TextWord? nearestWord = null;

            foreach (var word in layout.Words)
            {
                double dist = TextLayoutEngine.DistanceToWord(fix.Xpx, fix.Ypx, word);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearestWord = word;
                }
            }

            binding.DistanceToWord = minDist;

            if (nearestWord != null && minDist <= maxDistancePx)
            {
                binding.WordIndex = nearestWord.Index;
                binding.LineIndex = nearestWord.LineIndex;
            }
            else
            {
                // Привязка только к строке
                var nearestLine = TextLayoutEngine.FindLineAt(layout, fix.Ypx);
                binding.LineIndex = nearestLine?.Index;
            }

            bindings.Add(binding);
        }

        return bindings;
    }

    /// <summary>
    /// Вычисляет полный набор метрик чтения
    /// </summary>
    public static ReadingAnalysisResult ComputeMetrics(
        IReadOnlyList<Fixation> fixations,
        TextLayoutResult layout,
        TextAnalysisSettings settings,
        int screenWmm = 0,
        int screenHmm = 0,
        int screenWpx = 1920,
        int screenHpx = 1080,
        float distanceM = 0.6f)
    {
        var result = new ReadingAnalysisResult();

        if (layout.IsEmpty || fixations.Count == 0)
            return result;

        // Фильтруем фиксации по длительности
        var filtered = fixations
            .Where(f => f.DurSec >= settings.MinFixationDurationSec &&
                        f.DurSec <= settings.MaxFixationDurationSec)
            .ToList();

        result.TotalFixations = filtered.Count;

        // Привязываем фиксации к тексту
        var bindings = BindFixationsToText(filtered, layout, settings.MaxFixationDistancePx);
        result.Bindings = bindings.ToArray();

        // Считаем фиксации на словах
        result.FixationsOnWords = bindings.Count(b => b.WordIndex.HasValue);

        // Вычисляем метрики по словам
        result.WordMetrics = ComputeWordMetrics(bindings, layout);

        // Вычисляем метрики по строкам
        result.LineMetrics = ComputeLineMetrics(bindings, layout, result.WordMetrics);

        // Вычисляем метрики саккад
        result.SaccadeMetrics = ComputeSaccadeMetrics(
            bindings, layout, screenWmm, screenHmm, screenWpx, screenHpx, distanceM);

        return result;
    }

    /// <summary>
    /// Вычисляет метрики чтения для каждого слова
    /// </summary>
    public static WordReadingMetrics[] ComputeWordMetrics(
        List<FixationTextBinding> bindings,
        TextLayoutResult layout)
    {
        // Инициализируем метрики для всех слов
        var metrics = layout.Words.Select(w => new WordReadingMetrics
        {
            WordIndex = w.Index,
            WordText = w.Text,
            LineIndex = w.LineIndex
        }).ToArray();

        if (bindings.Count == 0)
        {
            // Все слова пропущены
            foreach (var m in metrics)
                m.WasSkipped = true;
            return metrics;
        }

        // Группируем фиксации по словам
        var wordFixations = bindings
            .Where(b => b.WordIndex.HasValue)
            .GroupBy(b => b.WordIndex!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Fixation.StartSec).ToList());

        // Определяем "правую границу" чтения для каждой фиксации
        // Это нужно для определения first-pass vs second-pass
        int maxWordReached = -1;
        var firstPassEnd = new Dictionary<int, int>(); // wordIndex -> sequenceIndex когда закончился first-pass

        foreach (var b in bindings.OrderBy(b => b.SequenceIndex))
        {
            if (b.WordIndex.HasValue)
            {
                int wordIdx = b.WordIndex.Value;

                // Если это первое посещение слова и мы уже были правее - это second-pass
                if (!firstPassEnd.ContainsKey(wordIdx))
                {
                    if (wordIdx < maxWordReached)
                    {
                        // Первая фиксация на этом слове уже является second-pass
                        firstPassEnd[wordIdx] = -1; // Нет first-pass
                    }
                }

                // Обновляем максимальную достигнутую позицию
                if (wordIdx > maxWordReached)
                    maxWordReached = wordIdx;
            }
        }

        // Теперь вычисляем метрики
        foreach (var (wordIdx, fixes) in wordFixations)
        {
            var m = metrics[wordIdx];
            var word = layout.Words[wordIdx];

            m.FixationCount = fixes.Count;
            m.TotalFixationDuration = fixes.Sum(f => f.Fixation.DurSec);

            // First Fixation Duration
            var firstFix = fixes[0];
            m.FirstFixationDuration = firstFix.Fixation.DurSec;

            // Initial Landing Position (0-1, относительно слова)
            if (word.Width > 0)
            {
                m.InitialLandingPosition = Math.Clamp(
                    (firstFix.Fixation.Xpx - word.X) / word.Width, 0, 1);
            }

            // Initial Landing Position в символах
            m.InitialLandingPositionChar = m.InitialLandingPosition * word.Text.Length;

            // First of Many Duration
            if (fixes.Count > 1)
            {
                m.FirstOfManyDuration = firstFix.Fixation.DurSec;
                m.WasRefixated = true;
            }

            // Определяем first-pass vs second-pass фиксации
            bool hasFirstPass = !firstPassEnd.TryGetValue(wordIdx, out int fpEnd) || fpEnd != -1;

            if (hasFirstPass)
            {
                // Находим момент, когда взгляд впервые ушёл правее этого слова
                int firstExitRight = -1;
                for (int i = 0; i < bindings.Count; i++)
                {
                    var b = bindings[i];
                    if (b.WordIndex.HasValue && b.WordIndex.Value > wordIdx)
                    {
                        // Проверяем, были ли фиксации на текущем слове до этого
                        bool hadFixOnWord = bindings.Take(i).Any(
                            bb => bb.WordIndex == wordIdx);
                        if (hadFixOnWord)
                        {
                            firstExitRight = i;
                            break;
                        }
                    }
                }

                // Gaze Duration = сумма фиксаций до первого выхода правее
                double gazeDur = 0;
                double goPastDur = 0;
                bool countingGoPast = false;

                foreach (var f in fixes)
                {
                    if (firstExitRight < 0 || f.SequenceIndex < firstExitRight)
                    {
                        gazeDur += f.Fixation.DurSec;
                    }
                    else
                    {
                        m.SecondPassDuration += f.Fixation.DurSec;
                    }
                }

                m.GazeDuration = gazeDur;

                // Go-past Duration: время от первой фиксации до выхода правее
                // включая регрессии внутри этого интервала
                if (firstExitRight > 0)
                {
                    int firstOnWord = fixes[0].SequenceIndex;
                    for (int i = firstOnWord; i < firstExitRight && i < bindings.Count; i++)
                    {
                        goPastDur += bindings[i].Fixation.DurSec;
                    }
                    m.GoPastDuration = goPastDur;
                }
                else
                {
                    // Не вышли правее - go-past = total duration текущего слова
                    m.GoPastDuration = m.TotalFixationDuration;
                }
            }
            else
            {
                // Весь second-pass
                m.SecondPassDuration = m.TotalFixationDuration;
            }
        }

        // Помечаем пропущенные слова
        foreach (var m in metrics)
        {
            m.WasSkipped = m.FixationCount == 0;
        }

        // Считаем регрессии
        ComputeRegressions(bindings, metrics);

        return metrics;
    }

    /// <summary>
    /// Вычисляет метрики для строк
    /// </summary>
    public static LineReadingMetrics[] ComputeLineMetrics(
        List<FixationTextBinding> bindings,
        TextLayoutResult layout,
        WordReadingMetrics[] wordMetrics)
    {
        var metrics = layout.Lines.Select(l => new LineReadingMetrics
        {
            LineIndex = l.Index,
            WordCount = l.Words.Count
        }).ToArray();

        // Группируем фиксации по строкам
        var lineFixations = bindings
            .Where(b => b.LineIndex.HasValue)
            .GroupBy(b => b.LineIndex!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Fixation.StartSec).ToList());

        foreach (var (lineIdx, fixes) in lineFixations)
        {
            if (lineIdx < 0 || lineIdx >= metrics.Length) continue;

            var m = metrics[lineIdx];
            m.FixationCount = fixes.Count;
            m.TotalFixationDuration = fixes.Sum(f => f.Fixation.DurSec);

            if (fixes.Count > 0)
            {
                m.FirstFixationTime = fixes[0].Fixation.StartSec;
                m.LastFixationTime = fixes[^1].Fixation.StartSec + fixes[^1].Fixation.DurSec;
            }
        }

        // Считаем пропущенные слова из wordMetrics
        foreach (var wm in wordMetrics)
        {
            if (wm.LineIndex >= 0 && wm.LineIndex < metrics.Length && wm.WasSkipped)
            {
                metrics[wm.LineIndex].SkippedWordCount++;
            }
        }

        // Reading Order Score
        foreach (var (lineIdx, fixes) in lineFixations)
        {
            if (lineIdx < 0 || lineIdx >= metrics.Length) continue;

            var m = metrics[lineIdx];
            m.ReadingOrderScore = CalculateReadingOrderScore(fixes, layout);
        }

        return metrics;
    }

    /// <summary>
    /// Вычисляет метрики саккад
    /// </summary>
    public static TextSaccadeMetrics ComputeSaccadeMetrics(
        List<FixationTextBinding> bindings,
        TextLayoutResult layout,
        int screenWmm, int screenHmm,
        int screenWpx, int screenHpx,
        float distanceM)
    {
        var metrics = new TextSaccadeMetrics();

        if (bindings.Count < 2)
            return metrics;

        var sorted = bindings.OrderBy(b => b.Fixation.StartSec).ToList();

        double mmPerPxX = screenWmm > 0 ? (double)screenWmm / screenWpx : 0;
        double mmPerPxY = screenHmm > 0 ? (double)screenHmm / screenHpx : 0;
        double distMm = distanceM * 1000;

        var amplitudesPx = new List<double>();
        var amplitudesDeg = new List<double>();
        var velocitiesDegS = new List<double>();

        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1];
            var curr = sorted[i];

            float dx = curr.Fixation.Xpx - prev.Fixation.Xpx;
            float dy = curr.Fixation.Ypx - prev.Fixation.Ypx;

            double distPx = Math.Sqrt(dx * dx + dy * dy);
            amplitudesPx.Add(distPx);

            metrics.TotalSaccades++;

            // Классификация саккады
            if (prev.LineIndex.HasValue && curr.LineIndex.HasValue)
            {
                int lineDiff = curr.LineIndex.Value - prev.LineIndex.Value;

                if (lineDiff != 0)
                {
                    // Переход между строками
                    metrics.SweepSaccades++;
                }
                else if (prev.WordIndex.HasValue && curr.WordIndex.HasValue)
                {
                    int wordDiff = curr.WordIndex.Value - prev.WordIndex.Value;
                    if (wordDiff > 0)
                    {
                        metrics.ProgressiveSaccades++;
                    }
                    else if (wordDiff < 0)
                    {
                        metrics.RegressiveSaccades++;
                    }
                }
                else if (dx > 0)
                {
                    metrics.ProgressiveSaccades++;
                }
                else if (dx < 0)
                {
                    metrics.RegressiveSaccades++;
                }
            }
            else if (dx > 0)
            {
                metrics.ProgressiveSaccades++;
            }
            else if (dx < 0)
            {
                metrics.RegressiveSaccades++;
            }

            // Вычисляем угловые характеристики
            if (mmPerPxX > 0 && mmPerPxY > 0 && distMm > 0)
            {
                double dxMm = dx * mmPerPxX;
                double dyMm = dy * mmPerPxY;
                double dMm = Math.Sqrt(dxMm * dxMm + dyMm * dyMm);

                double angRad = Math.Atan2(dMm, distMm);
                double angDeg = angRad * 57.2957795;
                amplitudesDeg.Add(angDeg);

                // Скорость: длительность саккады = gap между фиксациями
                double dt = curr.Fixation.StartSec - (prev.Fixation.StartSec + prev.Fixation.DurSec);
                if (dt > 0.001)
                {
                    velocitiesDegS.Add(angDeg / dt);
                }
            }
        }

        if (amplitudesPx.Count > 0)
            metrics.MeanSaccadeAmplitudePx = amplitudesPx.Average();

        if (amplitudesDeg.Count > 0)
            metrics.MeanSaccadeAmplitudeDeg = amplitudesDeg.Average();

        if (velocitiesDegS.Count > 0)
            metrics.MeanSaccadeVelocityDegS = velocitiesDegS.Average();

        return metrics;
    }

    #region Private Methods

    private static void ComputeRegressions(
        List<FixationTextBinding> bindings,
        WordReadingMetrics[] metrics)
    {
        var sorted = bindings
            .Where(b => b.WordIndex.HasValue)
            .OrderBy(b => b.SequenceIndex)
            .ToList();

        for (int i = 1; i < sorted.Count; i++)
        {
            int prevWord = sorted[i - 1].WordIndex!.Value;
            int currWord = sorted[i].WordIndex!.Value;

            if (currWord < prevWord)
            {
                // Регрессия: ушли влево
                if (prevWord < metrics.Length)
                    metrics[prevWord].NumberOfRegressionsOut++;

                if (currWord < metrics.Length)
                    metrics[currWord].NumberOfRegressionsIn++;
            }
        }
    }

    private static double CalculateReadingOrderScore(
        List<FixationTextBinding> fixes,
        TextLayoutResult layout)
    {
        // Reading Order Score: насколько порядок фиксаций соответствует слева-направо
        // 1.0 = идеально по порядку, 0.0 = полностью обратный

        var wordFixes = fixes
            .Where(f => f.WordIndex.HasValue)
            .Select(f => f.WordIndex!.Value)
            .ToList();

        if (wordFixes.Count < 2)
            return 1.0;

        int correctOrder = 0;
        int totalPairs = 0;

        for (int i = 1; i < wordFixes.Count; i++)
        {
            totalPairs++;
            if (wordFixes[i] >= wordFixes[i - 1])
                correctOrder++;
        }

        return totalPairs > 0 ? (double)correctOrder / totalPairs : 1.0;
    }

    #endregion
}
