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
    /// Вычисляет метрики чтения для каждого слова (алгоритмы как в eyekit)
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
            foreach (var m in metrics)
                m.WasSkipped = true;
            return metrics;
        }

        // Сортируем фиксации по времени
        var sortedBindings = bindings.OrderBy(b => b.Fixation.StartSec).ToList();

        // Вычисляем метрики для каждого слова
        foreach (var word in layout.Words)
        {
            var m = metrics[word.Index];

            // number_of_fixations: количество фиксаций на слове
            m.FixationCount = CountFixationsOnWord(sortedBindings, word);

            if (m.FixationCount == 0)
            {
                m.WasSkipped = true;
                continue;
            }

            // total_fixation_duration: сумма всех фиксаций на слове
            m.TotalFixationDuration = TotalFixationDuration(sortedBindings, word);

            // initial_fixation_duration: длительность первой фиксации
            m.FirstFixationDuration = InitialFixationDuration(sortedBindings, word);

            // first_of_many_duration: длительность первой фиксации если их > 1
            var fom = FirstOfManyDuration(sortedBindings, word);
            if (fom.HasValue)
            {
                m.FirstOfManyDuration = fom.Value;
                m.WasRefixated = true;
            }

            // gaze_duration: сумма фиксаций до первого выхода из слова
            m.GazeDuration = GazeDuration(sortedBindings, word);

            // go_past_duration: время от входа до выхода ВПРАВО (включая регрессии)
            m.GoPastDuration = GoPastDuration(sortedBindings, word);

            // second_pass_duration: сумма фиксаций во втором проходе
            m.SecondPassDuration = SecondPassDuration(sortedBindings, word);

            // initial_landing_position и initial_landing_distance
            var (landingPosChar, landingPosFrac, landingDistPx) =
                InitialLandingPosition(sortedBindings, word);
            m.InitialLandingPositionChar = landingPosChar;
            m.InitialLandingPosition = landingPosFrac;
            m.InitialLandingDistancePx = landingDistPx;

            // number_of_regressions_in
            m.NumberOfRegressionsIn = NumberOfRegressionsIn(sortedBindings, word);
        }

        // Считаем регрессии OUT
        ComputeRegressionsOut(sortedBindings, metrics);

        return metrics;
    }

    #region Eyekit-style Measure Functions

    /// <summary>
    /// Проверяет, находится ли фиксация внутри слова (как eyekit "fixation in interest_area")
    /// </summary>
    private static bool IsFixationInWord(FixationTextBinding binding, TextWord word)
    {
        return binding.WordIndex == word.Index;
    }

    /// <summary>
    /// Проверяет, находится ли слово слева от фиксации (is_left_of в eyekit)
    /// </summary>
    private static bool IsWordLeftOf(TextWord word, FixationTextBinding binding)
    {
        // word.x_br < fixation.x (правый край слова левее X фиксации)
        return (word.X + word.Width) < binding.Fixation.Xpx;
    }

    /// <summary>
    /// Проверяет, находится ли слово перед фиксацией (is_before в eyekit для LTR)
    /// Для LTR текста: is_before = is_left_of
    /// </summary>
    private static bool IsWordBefore(TextWord word, FixationTextBinding binding)
    {
        return IsWordLeftOf(word, binding);
    }

    /// <summary>
    /// number_of_fixations (eyekit)
    /// </summary>
    private static int CountFixationsOnWord(List<FixationTextBinding> bindings, TextWord word)
    {
        int count = 0;
        foreach (var b in bindings)
        {
            if (IsFixationInWord(b, word))
                count++;
        }
        return count;
    }

    /// <summary>
    /// initial_fixation_duration (eyekit): длительность первой фиксации на слове
    /// </summary>
    private static double InitialFixationDuration(List<FixationTextBinding> bindings, TextWord word)
    {
        foreach (var b in bindings)
        {
            if (IsFixationInWord(b, word))
                return b.Fixation.DurSec;
        }
        return 0;
    }

    /// <summary>
    /// first_of_many_duration (eyekit): длительность первой фиксации, только если их > 1
    /// </summary>
    private static double? FirstOfManyDuration(List<FixationTextBinding> bindings, TextWord word)
    {
        double? duration = null;
        foreach (var b in bindings)
        {
            if (IsFixationInWord(b, word))
            {
                if (duration.HasValue)
                    return duration; // вторая фиксация найдена, возвращаем первую
                duration = b.Fixation.DurSec;
            }
        }
        return null; // только одна фиксация или ни одной
    }

    /// <summary>
    /// total_fixation_duration (eyekit): сумма всех фиксаций на слове
    /// </summary>
    private static double TotalFixationDuration(List<FixationTextBinding> bindings, TextWord word)
    {
        double duration = 0;
        foreach (var b in bindings)
        {
            if (IsFixationInWord(b, word))
                duration += b.Fixation.DurSec;
        }
        return duration;
    }

    /// <summary>
    /// gaze_duration (eyekit): сумма фиксаций до первого выхода из слова
    /// </summary>
    private static double GazeDuration(List<FixationTextBinding> bindings, TextWord word)
    {
        double duration = 0;
        foreach (var b in bindings)
        {
            if (IsFixationInWord(b, word))
            {
                duration += b.Fixation.DurSec;
            }
            else if (duration > 0)
            {
                // Была хотя бы одна фиксация на слове, и эта уже вне - выходим
                break;
            }
        }
        return duration;
    }

    /// <summary>
    /// go_past_duration (eyekit): время от входа до выхода ВПРАВО
    /// </summary>
    private static double GoPastDuration(List<FixationTextBinding> bindings, TextWord word)
    {
        double duration = 0;
        bool entered = false;
        foreach (var b in bindings)
        {
            if (IsFixationInWord(b, word))
            {
                entered = true;
                duration += b.Fixation.DurSec;
            }
            else if (entered)
            {
                // Вошли ранее, сейчас вне слова
                if (IsWordBefore(word, b))
                {
                    // Слово теперь слева от фиксации = вышли вправо
                    break;
                }
                // Вышли влево (регрессия), продолжаем считать
                duration += b.Fixation.DurSec;
            }
        }
        return duration;
    }

    /// <summary>
    /// second_pass_duration (eyekit): сумма фиксаций во втором проходе
    /// </summary>
    private static double SecondPassDuration(List<FixationTextBinding> bindings, TextWord word)
    {
        double duration = 0;
        int? currentPass = null;
        int nextPass = 1;

        foreach (var b in bindings)
        {
            if (IsFixationInWord(b, word))
            {
                if (!currentPass.HasValue)
                {
                    // Первая фиксация в новом проходе
                    currentPass = nextPass;
                }
                if (currentPass == 2)
                {
                    duration += b.Fixation.DurSec;
                }
            }
            else if (currentPass == 1)
            {
                // Первая фиксация, выходящая из первого прохода
                currentPass = null;
                nextPass++;
            }
            else if (currentPass == 2)
            {
                // Первая фиксация, выходящая из второго прохода
                break;
            }
        }
        return duration;
    }

    /// <summary>
    /// initial_landing_position и initial_landing_distance (eyekit)
    /// Возвращает (позиция в символах 1-based, позиция 0-1, расстояние в px)
    /// </summary>
    private static (int? charPos, double fracPos, double? distPx) InitialLandingPosition(
        List<FixationTextBinding> bindings, TextWord word)
    {
        foreach (var b in bindings)
        {
            if (IsFixationInWord(b, word))
            {
                // Расстояние от левого края слова
                double distPx = Math.Abs(b.Fixation.Xpx - word.X);

                // Позиция как доля (0-1)
                double fracPos = 0;
                if (word.Width > 0)
                {
                    fracPos = Math.Clamp((b.Fixation.Xpx - word.X) / word.Width, 0, 1);
                }

                // Позиция в символах (1-based как в eyekit)
                int charPos = 1;
                if (word.Text.Length > 0 && word.Width > 0)
                {
                    double charWidth = word.Width / word.Text.Length;
                    charPos = Math.Clamp(
                        (int)Math.Floor((b.Fixation.Xpx - word.X) / charWidth) + 1,
                        1, word.Text.Length);
                }

                return (charPos, fracPos, distPx);
            }
        }
        return (null, 0, null);
    }

    /// <summary>
    /// number_of_regressions_in (eyekit): количество регрессий обратно в слово
    /// </summary>
    private static int NumberOfRegressionsIn(List<FixationTextBinding> bindings, TextWord word)
    {
        // Найти индекс первого выхода из слова
        bool enteredWord = false;
        int? firstExitIndex = null;

        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            if (IsFixationInWord(b, word))
            {
                enteredWord = true;
            }
            else if (enteredWord)
            {
                firstExitIndex = i;
                break;
            }
        }

        if (!firstExitIndex.HasValue)
            return 0; // Слово никогда не покидали

        // Считаем регрессии после первого выхода
        int count = 0;
        for (int i = firstExitIndex.Value + 1; i < bindings.Count; i++)
        {
            var prev = bindings[i - 1];
            var curr = bindings[i];

            // Если предыдущая НЕ на слове, а текущая НА слове
            if (!IsFixationInWord(prev, word) && IsFixationInWord(curr, word))
            {
                // И фиксация движется влево (регрессия для LTR)
                if (curr.Fixation.Xpx < prev.Fixation.Xpx)
                {
                    count++;
                }
            }
        }
        return count;
    }

    #endregion

    /// <summary>
    /// Вычисляет регрессии OUT (из каждого слова)
    /// </summary>
    private static void ComputeRegressionsOut(
        List<FixationTextBinding> bindings,
        WordReadingMetrics[] metrics)
    {
        for (int i = 1; i < bindings.Count; i++)
        {
            var prev = bindings[i - 1];
            var curr = bindings[i];

            if (prev.WordIndex.HasValue && curr.WordIndex.HasValue)
            {
                if (curr.WordIndex.Value < prev.WordIndex.Value)
                {
                    // Регрессия влево
                    if (prev.WordIndex.Value < metrics.Length)
                        metrics[prev.WordIndex.Value].NumberOfRegressionsOut++;
                }
            }
        }
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
