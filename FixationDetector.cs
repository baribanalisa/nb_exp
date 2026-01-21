using System;
using System.Collections.Generic;

namespace NeuroBureau.Experiment;

public readonly record struct GazeSample(float TimeSec, float Xn, float Yn); // Xn/Yn = 0..1
public readonly record struct Fixation(float StartSec, float DurSec, float Xpx, float Ypx);

public static class FixationDetector
{
    // IDT: дисперсия = (maxX-minX) + (maxY-minY) в пикселях
    public static List<Fixation> DetectIdt(
        IReadOnlyList<GazeSample> s,
        int screenW, int screenH,
        float minFixDurSec = 0.08f,
        float dispersionThresholdPx = 60f)
    {
        var res = new List<Fixation>();
        if (s.Count < 2 || screenW <= 0 || screenH <= 0) return res;

        int i = 0;
        while (i < s.Count)
        {
            int j = i;

            while (j < s.Count && (s[j].TimeSec - s[i].TimeSec) < minFixDurSec) j++;
            if (j >= s.Count) break;

            GetMinMax(s, i, j, screenW, screenH, out float minX, out float maxX, out float minY, out float maxY);
            float disp = (maxX - minX) + (maxY - minY);

            if (disp <= dispersionThresholdPx)
            {
                int k = j;

                while (k + 1 < s.Count)
                {
                    float nx = s[k + 1].Xn * screenW;
                    float ny = s[k + 1].Yn * screenH;

                    float nMinX = Math.Min(minX, nx);
                    float nMaxX = Math.Max(maxX, nx);
                    float nMinY = Math.Min(minY, ny);
                    float nMaxY = Math.Max(maxY, ny);

                    float nDisp = (nMaxX - nMinX) + (nMaxY - nMinY);
                    if (nDisp > dispersionThresholdPx) break;

                    minX = nMinX; maxX = nMaxX; minY = nMinY; maxY = nMaxY;
                    k++;
                }

                float sumX = 0, sumY = 0;
                int cnt = 0;
                for (int m = i; m <= k; m++)
                {
                    sumX += s[m].Xn * screenW;
                    sumY += s[m].Yn * screenH;
                    cnt++;
                }

                float start = s[i].TimeSec;
                float end = s[k].TimeSec;
                float dur = Math.Max(0, end - start);

                if (dur >= minFixDurSec)
                    res.Add(new Fixation(start, dur, sumX / cnt, sumY / cnt));

                i = k + 1;
            }
            else i++;
        }

        return res;
    }

    private static void GetMinMax(IReadOnlyList<GazeSample> s, int i, int j, int w, int h,
        out float minX, out float maxX, out float minY, out float maxY)
    {
        float x0 = s[i].Xn * w;
        float y0 = s[i].Yn * h;
        minX = maxX = x0;
        minY = maxY = y0;

        for (int k = i + 1; k <= j; k++)
        {
            float x = s[k].Xn * w;
            float y = s[k].Yn * h;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
    }
}
