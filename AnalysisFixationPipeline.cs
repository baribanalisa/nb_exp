using System;
using System.Collections.Generic;
using System.Linq;

namespace NeuroBureau.Experiment;

internal readonly struct RawGazeSample
{
    public readonly float TimeSec;
    public readonly float Xn;
    public readonly float Yn;
    public readonly float DistanceM;
    public readonly bool Valid;
    public readonly bool OpenValid;

    public RawGazeSample(float timeSec, float xn, float yn, float distanceM, bool valid, bool openValid)
    {
        TimeSec = timeSec;
        Xn = xn;
        Yn = yn;
        DistanceM = distanceM;
        Valid = valid;
        OpenValid = openValid;
    }

    public RawGazeSample With(float? xn = null, float? yn = null, bool? valid = null, bool? openValid = null)
        => new RawGazeSample(TimeSec,
                             xn ?? Xn,
                             yn ?? Yn,
                             DistanceM,
                             valid ?? Valid,
                             openValid ?? OpenValid);
}

internal static class AnalysisFixationPipeline
{
    public static List<RawGazeSample> Preprocess(IReadOnlyList<RawGazeSample> input, AnalysisDetectionSettings cfg)
    {
        if (input.Count == 0) return new List<RawGazeSample>();

        // копия, чтобы можно было менять
        var s = input.ToArray();

        // 1) Gap filling (интерполяция коротких "дыр")
        if (cfg.GapWindowSize > 0)
            GapFillInPlace(s, cfg.GapWindowSize);

        // 2) Noise reduction
        if (cfg.NoiseReduction != NoiseReductionType.None && cfg.WindowSize > 1)
            s = Filter(s, cfg.NoiseReduction, cfg.WindowSize);

        return s.ToList();
    }

    private static void GapFillInPlace(RawGazeSample[] s, int maxGapSamples)
    {
        const float MaxGapSec = 0.25f;

        int n = s.Length;

        int i = 0;
        while (i < n)
        {
            // ищем начало "дыры"
            while (i < n && s[i].Valid) i++;
            if (i >= n) break;

            int gapStart = i;
            while (i < n && !s[i].Valid) i++;
            int gapEnd = i; // первый валидный после дыры, или n

            int gapLen = gapEnd - gapStart;
            if (gapLen <= 0 || gapLen > maxGapSamples) continue;

            int left = gapStart - 1;
            int right = gapEnd;
            if (left < 0 || right >= n) continue;
            if (!s[left].Valid || !s[right].Valid) continue;

            // Не интерполируем, если это похоже на blink: нам важен только флаг OPEN_VALID.
            if (!s[left].OpenValid || !s[right].OpenValid) continue;
            bool blinkInside = false;
            for (int j = gapStart; j < gapEnd; j++)
            {
                if (!s[j].OpenValid) { blinkInside = true; break; }
            }
            if (blinkInside) continue;

            // Ограничение по времени, чтобы не "склеивать" длинные провалы.
            float dtGap = s[right].TimeSec - s[left].TimeSec;
            if (!float.IsFinite(dtGap) || dtGap <= 0 || dtGap > MaxGapSec) continue;

            float x0 = s[left].Xn, y0 = s[left].Yn;
            float x1 = s[right].Xn, y1 = s[right].Yn;

            if (!float.IsFinite(x0) || !float.IsFinite(y0) || !float.IsFinite(x1) || !float.IsFinite(y1)) continue;

            for (int k = 1; k <= gapLen; k++)
            {
                float a = (float)k / (gapLen + 1);
                float x = x0 + (x1 - x0) * a;
                float y = y0 + (y1 - y0) * a;
                if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
                if (x < 0 || x > 1 || y < 0 || y > 1) continue;
                int idx = gapStart + (k - 1);
                s[idx] = s[idx].With(xn: x, yn: y, valid: true, openValid: true);
            }
        }
    }

    private static RawGazeSample[] Filter(RawGazeSample[] s, NoiseReductionType type, int window)
    {
        int n = s.Length;
        int half = Math.Max(0, window / 2);

        var outArr = new RawGazeSample[n];
        Array.Copy(s, outArr, n);

        var tmpX = new List<float>(window);
        var tmpY = new List<float>(window);

        for (int i = 0; i < n; i++)
        {
            if (!s[i].Valid) continue;

            tmpX.Clear();
            tmpY.Clear();

            int a = Math.Max(0, i - half);
            int b = Math.Min(n - 1, i + half);

            for (int j = a; j <= b; j++)
            {
                if (!s[j].Valid) continue;
                float x = s[j].Xn;
                float y = s[j].Yn;
                if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
                if (x < 0 || x > 1 || y < 0 || y > 1) continue;
                tmpX.Add(x);
                tmpY.Add(y);
            }

            if (tmpX.Count == 0) continue;

            float nx, ny;
            if (type == NoiseReductionType.Median)
            {
                nx = Median(tmpX);
                ny = Median(tmpY);
            }
            else
            {
                nx = tmpX.Average();
                ny = tmpY.Average();
            }

            if (!float.IsFinite(nx) || !float.IsFinite(ny) || nx < 0 || nx > 1 || ny < 0 || ny > 1)
            {
                // если фильтр дал мусор — лучше не портить исходную точку
                continue;
            }

            outArr[i] = outArr[i].With(xn: nx, yn: ny);
        }

        return outArr;

        static float Median(List<float> v)
        {
            v.Sort();
            int m = v.Count / 2;
            if ((v.Count & 1) == 1) return v[m];
            return (v[m - 1] + v[m]) * 0.5f;
        }
    }

    public static List<Fixation> DetectIdt(IReadOnlyList<RawGazeSample> raw, int screenW, int screenH, AnalysisDetectionSettings cfg)
    {
        // работаем только по валидным (после gap fill они могут стать валидными)
        var samples = new List<(float t, float xpx, float ypx)>(raw.Count);
        foreach (var r in raw)
        {
            if (!r.Valid) continue;
            if (!float.IsFinite(r.TimeSec) || !float.IsFinite(r.Xn) || !float.IsFinite(r.Yn)) continue;
            if (r.Xn < 0 || r.Xn > 1 || r.Yn < 0 || r.Yn > 1) continue;
            samples.Add((r.TimeSec, r.Xn * screenW, r.Yn * screenH));
        }

        if (samples.Count == 0) return new List<Fixation>();

        float dispThr = (float)Math.Max(0.01, cfg.IdtDispersionThresholdPx);
        float minDur = Math.Max(0.001f, cfg.IdtMinDurationMs / 1000f);
        float minWin = Math.Max(0.001f, cfg.IdtWindowMs / 1000f);

        var fix = new List<Fixation>();

        int n = samples.Count;
        int i = 0;
        while (i < n)
        {
            int j = i;
            while (j < n && (samples[j].t - samples[i].t) < minWin) j++;

            if (j >= n) break;

            if (ComputeDispersion(samples, i, j) <= dispThr)
            {
                // расширяем окно, пока держится порог
                int k = j;
                while (k + 1 < n && ComputeDispersion(samples, i, k + 1) <= dispThr)
                    k++;

                float tStart = samples[i].t;
                float tEnd = samples[k].t;
                float dur = tEnd - tStart;

                if (dur >= minDur)
                {
                    (float cx, float cy) = MeanXY(samples, i, k);
                    fix.Add(new Fixation(tStart, dur, cx, cy));
                }

                i = k + 1;
            }
            else
            {
                i++;
            }
        }

        // merge (по времени) — пока без MergeDistance
        if (cfg.IdtMergeTimeMs > 0 && fix.Count > 1)
        {
            float mergeSec = cfg.IdtMergeTimeMs / 1000f;
            fix = MergeByTime(fix, mergeSec);
        }

        fix.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return fix;

        static float ComputeDispersion(List<(float t, float xpx, float ypx)> s, int a, int b)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            for (int i = a; i <= b; i++)
            {
                var p = s[i];
                if (p.xpx < minX) minX = p.xpx;
                if (p.xpx > maxX) maxX = p.xpx;
                if (p.ypx < minY) minY = p.ypx;
                if (p.ypx > maxY) maxY = p.ypx;
            }

            return (maxX - minX) + (maxY - minY);
        }

        static (float cx, float cy) MeanXY(List<(float t, float xpx, float ypx)> s, int a, int b)
        {
            double sx = 0, sy = 0;
            int cnt = 0;
            for (int i = a; i <= b; i++)
            {
                sx += s[i].xpx;
                sy += s[i].ypx;
                cnt++;
            }
            if (cnt <= 0) return (0, 0);
            return ((float)(sx / cnt), (float)(sy / cnt));
        }
    }

    public static List<Fixation> DetectIvt(IReadOnlyList<RawGazeSample> raw,
        int screenW, int screenH, int screenWmm, int screenHmm, AnalysisDetectionSettings cfg)
    {
        // Если нет физики экрана — угловую скорость нормально не посчитать.
        if (screenW <= 0 || screenH <= 0 || screenWmm <= 0 || screenHmm <= 0)
            return new List<Fixation>();

        float mmPerPxX = screenWmm / (float)screenW;
        float mmPerPxY = screenHmm / (float)screenH;

        // работаем только по валидным
        var s = new List<RawGazeSample>(raw.Count);
        foreach (var r in raw)
        {
            if (!r.Valid) continue;
            if (!float.IsFinite(r.TimeSec) || !float.IsFinite(r.Xn) || !float.IsFinite(r.Yn) || !float.IsFinite(r.DistanceM)) continue;
            if (r.Xn < 0 || r.Xn > 1 || r.Yn < 0 || r.Yn > 1) continue;
            s.Add(r);
        }

        if (s.Count < 2) return new List<Fixation>();

        float vFix = (float)Math.Max(0.01, cfg.IvtSpeedFixDegPerSec);
        float minDur = Math.Max(0.001f, cfg.IvtMinDurationMs / 1000f);

        // 1) считаем скорость между соседними
        var vel = new float[s.Count];
        vel[0] = float.PositiveInfinity;

        const float MaxSpeedDegPerSec = 800f;
        const float MaxDtSec = 0.25f;

        for (int i = 1; i < s.Count; i++)
        {
            float dt = s[i].TimeSec - s[i - 1].TimeSec;
            if (!float.IsFinite(dt) || dt <= 0 || dt > MaxDtSec) { vel[i] = float.PositiveInfinity; continue; }

            // distance до экрана (мм)
            float distM = (s[i].DistanceM > 0 ? s[i].DistanceM : s[i - 1].DistanceM);
            if (!float.IsFinite(distM) || distM <= 0) { vel[i] = float.PositiveInfinity; continue; }
            float distMm = distM * 1000f;

            float x0px = s[i - 1].Xn * screenW;
            float y0px = s[i - 1].Yn * screenH;
            float x1px = s[i].Xn * screenW;
            float y1px = s[i].Yn * screenH;

            float dxMm = (x1px - x0px) * mmPerPxX;
            float dyMm = (y1px - y0px) * mmPerPxY;

            float dMm = (float)Math.Sqrt(dxMm * dxMm + dyMm * dyMm);
            float angRad = (float)Math.Atan2(dMm, distMm);
            float angDeg = angRad * 57.2957795f;

            float v = angDeg / dt;
            vel[i] = (float.IsFinite(v) && v <= MaxSpeedDegPerSec) ? v : float.PositiveInfinity;
        }

        // 2) детекция: velocity <= vFix -> фиксация
        var fixes = new List<Fixation>();

        int start = -1;
        for (int i = 1; i < s.Count; i++)
        {
            bool isFix = vel[i] <= vFix;

            if (isFix)
            {
                if (start < 0) start = i - 1; // стартуем с предыдущего сэмпла
            }
            else
            {
                if (start >= 0)
                {
                    int end = i - 1;
                    AddFixIfOk(s, start, end, minDur, screenW, screenH, fixes);
                    start = -1;
                }
            }
        }

        if (start >= 0)
            AddFixIfOk(s, start, s.Count - 1, minDur, screenW, screenH, fixes);

        // 3) join
        if (cfg.IvtJoinType != JoinFixType.DontJoinFix && fixes.Count > 1)
        {
            float mergeSec = cfg.IvtMergeTimeMs / 1000f;
            float mergeAngle = (float)Math.Max(0, cfg.IvtMergeAngleDeg);
            fixes = JoinFixes(fixes, s, screenW, screenH, screenWmm, screenHmm, cfg.IvtJoinType, mergeSec, mergeAngle);
        }

        fixes.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return fixes;
    }

    private static void AddFixIfOk(List<RawGazeSample> s, int a, int b, float minDur, int screenW, int screenH, List<Fixation> outFix)
    {
        float t0 = s[a].TimeSec;
        float t1 = s[b].TimeSec;
        float dur = t1 - t0;
        if (dur < minDur) return;

        double sx = 0, sy = 0;
        int cnt = 0;
        for (int i = a; i <= b; i++)
        {
            sx += s[i].Xn * screenW;
            sy += s[i].Yn * screenH;
            cnt++;
        }

        if (cnt <= 0) return;

        outFix.Add(new Fixation(t0, dur, (float)(sx / cnt), (float)(sy / cnt)));
    }

    private static List<Fixation> MergeByTime(List<Fixation> inFix, float mergeSec)
    {
        if (inFix.Count == 0) return inFix;

        var sorted = inFix.OrderBy(f => f.StartSec).ToList();
        var res = new List<Fixation>(sorted.Count);

        Fixation cur = sorted[0];
        float curEnd = cur.StartSec + cur.DurSec;

        for (int i = 1; i < sorted.Count; i++)
        {
            var nxt = sorted[i];
            float gap = nxt.StartSec - curEnd;

            if (gap <= mergeSec)
            {
                float nxtEnd = nxt.StartSec + nxt.DurSec;
                float newEnd = Math.Max(curEnd, nxtEnd);

                // центр — по длительности
                float w1 = Math.Max(0.0001f, cur.DurSec);
                float w2 = Math.Max(0.0001f, nxt.DurSec);
                float cx = (cur.Xpx * w1 + nxt.Xpx * w2) / (w1 + w2);
                float cy = (cur.Ypx * w1 + nxt.Ypx * w2) / (w1 + w2);

                cur = new Fixation(cur.StartSec, newEnd - cur.StartSec, cx, cy);
                curEnd = newEnd;
            }
            else
            {
                res.Add(cur);
                cur = nxt;
                curEnd = cur.StartSec + cur.DurSec;
            }
        }

        res.Add(cur);
        return res;
    }

    private static List<Fixation> JoinFixes(
        List<Fixation> fixes,
        List<RawGazeSample> samples,
        int screenW, int screenH, int screenWmm, int screenHmm,
        JoinFixType type,
        float mergeSec,
        float mergeAngleDeg)
    {
        if (type == JoinFixType.DontJoinFix) return fixes;
        if (fixes.Count == 0) return fixes;

        var sorted = fixes.OrderBy(f => f.StartSec).ToList();
        var res = new List<Fixation>(sorted.Count);

        Fixation cur = sorted[0];
        float curEnd = cur.StartSec + cur.DurSec;

        for (int i = 1; i < sorted.Count; i++)
        {
            var nxt = sorted[i];
            float gap = nxt.StartSec - curEnd;

            bool canJoin = gap <= mergeSec;

            if (canJoin && type == JoinFixType.JoinFixByTimeAngle)
            {
                // угол между центрами фиксаций
                canJoin = AngleBetweenFixationsDeg(cur, nxt, samples, screenW, screenH, screenWmm, screenHmm) <= mergeAngleDeg;
            }

            if (canJoin)
            {
                float nxtEnd = nxt.StartSec + nxt.DurSec;
                float newEnd = Math.Max(curEnd, nxtEnd);

                float w1 = Math.Max(0.0001f, cur.DurSec);
                float w2 = Math.Max(0.0001f, nxt.DurSec);
                float cx = (cur.Xpx * w1 + nxt.Xpx * w2) / (w1 + w2);
                float cy = (cur.Ypx * w1 + nxt.Ypx * w2) / (w1 + w2);

                cur = new Fixation(cur.StartSec, newEnd - cur.StartSec, cx, cy);
                curEnd = newEnd;
            }
            else
            {
                res.Add(cur);
                cur = nxt;
                curEnd = cur.StartSec + cur.DurSec;
            }
        }

        res.Add(cur);
        return res;
    }

    private static float AngleBetweenFixationsDeg(
        Fixation a,
        Fixation b,
        List<RawGazeSample> samples,
        int screenW, int screenH, int screenWmm, int screenHmm)
    {
        // берём усреднённую дистанцию из сэмплов внутри первой фиксации (если не нашли — 0)
        float distA = MeanDistanceMForFix(samples, a);
        float distB = MeanDistanceMForFix(samples, b);
        float dist = distA > 0 ? distA : distB;
        if (dist <= 0) return float.PositiveInfinity;

        float mmPerPxX = screenWmm / (float)screenW;
        float mmPerPxY = screenHmm / (float)screenH;

        float dxMm = (b.Xpx - a.Xpx) * mmPerPxX;
        float dyMm = (b.Ypx - a.Ypx) * mmPerPxY;
        float dMm = (float)Math.Sqrt(dxMm * dxMm + dyMm * dyMm);

        float angRad = (float)Math.Atan2(dMm, dist * 1000f);
        return angRad * 57.2957795f;
    }

    private static float MeanDistanceMForFix(List<RawGazeSample> samples, Fixation f)
    {
        float t0 = f.StartSec;
        float t1 = f.StartSec + f.DurSec;

        double sum = 0;
        int cnt = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (!s.Valid) continue;
            if (s.TimeSec < t0 || s.TimeSec > t1) continue;
            if (s.DistanceM <= 0) continue;
            sum += s.DistanceM;
            cnt++;
        }

        if (cnt == 0) return 0;
        return (float)(sum / cnt);
    }
}
