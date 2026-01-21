
using System;
using System.Runtime.InteropServices;

namespace NeuroBureau.Experiment;

public static class ScreenUtils
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    private const int HORZSIZE = 4;      // Width in millimeters
    private const int VERTSIZE = 6;      // Height in millimeters
    private const int HORZRES = 8;       // Width in pixels
    private const int VERTRES = 10;      // Height in pixels

    public record struct ScreenPhysicalSize(int WidthMm, int HeightMm, int WidthPx, int HeightPx);

    public static ScreenPhysicalSize GetPrimaryScreenPhysical()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            int wMm = GetDeviceCaps(hdc, HORZSIZE);
            int hMm = GetDeviceCaps(hdc, VERTSIZE);
            int wPx = GetDeviceCaps(hdc, HORZRES);
            int hPx = GetDeviceCaps(hdc, VERTRES);
            return new ScreenPhysicalSize(wMm, hMm, wPx, hPx);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }
}