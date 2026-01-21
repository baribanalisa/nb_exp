using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Management;

namespace NeuroBureau.Experiment;

public sealed class DisplayMonitor
{
    public int Index { get; init; }
    public IntPtr Handle { get; init; }
    public string DeviceName { get; init; } = "";     // \\.\DISPLAY1
    public string FriendlyName { get; init; } = "";   // "DELL U2720Q" (если получилось)
    public bool IsPrimary { get; init; }

    public Rect Bounds { get; init; }
    public Rect WorkArea { get; init; }

    public int WidthPx { get; init; }
    public int HeightPx { get; init; }

    public int WidthMm { get; init; }
    public int HeightMm { get; init; }

    public string ToUiString()
    {
        var title = string.IsNullOrWhiteSpace(FriendlyName) ? DeviceName : $"{FriendlyName} ({DeviceName})";
        var prim = IsPrimary ? " • primary" : "";
        var mm = (WidthMm > 0 && HeightMm > 0) ? $" • {WidthMm}×{HeightMm} мм" : "";
        return $"{Index}: {title} • {WidthPx}×{HeightPx}px{mm}{prim}";
    }
}


public static class MonitorService
{
    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        // 1) то, что даёт EnumDisplayMonitors (в clone может быть 1 штука)
        var rawMonitors = new List<(IntPtr hMon, string dev, bool primary, Rect bounds, Rect work)>();

        bool Callback(IntPtr hMon, IntPtr hdc, ref RECT lprc, IntPtr data)
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (!GetMonitorInfo(hMon, ref mi))
                return true;

            var bounds = RectFrom(mi.rcMonitor);
            var work = RectFrom(mi.rcWork);
            var isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;

            rawMonitors.Add((hMon, mi.szDevice ?? "", isPrimary, bounds, work));
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        var rawByDev = rawMonitors
            .Where(r => !string.IsNullOrWhiteSpace(r.dev))
            .GroupBy(r => r.dev, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // 2) то, что даёт EnumDisplayDevices (обычно видит DISPLAY1/2 и при дублировании)
        var devs = EnumerateAttachedDisplays(); // (devName, friendly)

        // если EnumDisplayDevices ничего не дал — fallback на старое поведение
        var baseList = (devs.Count > 0)
            ? devs
            : rawMonitors
                .Select(r => (dev: r.dev, friendly: ""))
                .DistinctBy(x => x.dev, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // стабилизируем порядок DISPLAY1, DISPLAY2...
        baseList.Sort((a, b) => ExtractDisplayNumber(a.dev).CompareTo(ExtractDisplayNumber(b.dev)));

        var list = new List<DisplayMonitor>();

        for (int i = 0; i < baseList.Count; i++)
        {
            var devName = baseList[i].dev;
            var friendly = baseList[i].friendly;

            IntPtr hMon = IntPtr.Zero;
            Rect bounds = Rect.Empty;
            Rect work = Rect.Empty;
            bool isPrimary = false;

            if (rawByDev.TryGetValue(devName, out var info))
            {
                hMon = info.hMon;
                bounds = info.bounds;
                work = info.work;
                isPrimary = info.primary;
            }

            // --- PX ---
            int wPx = 0, hPx = 0;
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            if (EnumDisplaySettings(devName, ENUM_CURRENT_SETTINGS, ref dm))
            {
                wPx = dm.dmPelsWidth;
                hPx = dm.dmPelsHeight;
            }

            if (wPx <= 0 || hPx <= 0)
            {
                IntPtr hdc = CreateDC("DISPLAY", devName, null, IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    try
                    {
                        wPx = GetDeviceCaps(hdc, HORZRES);
                        hPx = GetDeviceCaps(hdc, VERTRES);
                    }
                    finally { DeleteDC(hdc); }
                }
            }

            if (wPx <= 0 || hPx <= 0)
            {
                IntPtr hdc0 = GetDC(IntPtr.Zero);
                try
                {
                    wPx = GetDeviceCaps(hdc0, DESKTOPHORZRES);
                    hPx = GetDeviceCaps(hdc0, DESKTOPVERTRES);

                    if (wPx <= 0 || hPx <= 0)
                    {
                        wPx = GetDeviceCaps(hdc0, HORZRES);
                        hPx = GetDeviceCaps(hdc0, VERTRES);
                    }
                }
                finally { ReleaseDC(IntPtr.Zero, hdc0); }
            }

            // --- MM ---
            int wMm = 0, hMm = 0;
            IntPtr hdcMm = CreateDC("DISPLAY", devName, null, IntPtr.Zero);
            if (hdcMm != IntPtr.Zero)
            {
                try
                {
                    wMm = GetDeviceCaps(hdcMm, HORZSIZE);
                    hMm = GetDeviceCaps(hdcMm, VERTSIZE);
                }
                finally { DeleteDC(hdcMm); }
            }

            // fallback мм через DPI (только если есть handle из EnumDisplayMonitors)
            if ((wMm <= 0 || hMm <= 0) && hMon != IntPtr.Zero && wPx > 0 && hPx > 0)
            {
                if (TryGetMmFromMonitorDpi(hMon, wPx, hPx, out var wMm2, out var hMm2))
                {
                    wMm = wMm2;
                    hMm = hMm2;
                }
            }

            list.Add(new DisplayMonitor
            {
                Index = i,
                Handle = hMon,
                DeviceName = devName,
                FriendlyName = friendly,
                IsPrimary = isPrimary,
                Bounds = bounds,
                WorkArea = work,
                WidthPx = wPx,
                HeightPx = hPx,
                WidthMm = wMm,
                HeightMm = hMm
            });
        }

                // Если система в "Дублировать", EnumDisplayMonitors часто отдаёт 1 логический монитор.
        // Тогда берём физические мониторы через WMI, чтобы можно было выбрать нужный и сохранить его мм.
        if (list.Count == 1)
        {
            var wmiMons = TryGetWmiActiveMonitors();
            if (wmiMons.Count >= 2)
            {
                var baseMon = list[0];
                var wmiList = new List<DisplayMonitor>();

                for (int i = 0; i < wmiMons.Count; i++)
                {
                    var wm = wmiMons[i];
                    wmiList.Add(new DisplayMonitor
                    {
                        Index = i,
                        Handle = baseMon.Handle,               // логический монитор один — ок
                        DeviceName = wm.DisplayText,           // показываем человеку нормальное имя/серийник
                        IsPrimary = (i == 0),

                        Bounds = baseMon.Bounds,
                        WorkArea = baseMon.WorkArea,

                        // px в дублировании одинаковые (единый desktop)
                        WidthPx = baseMon.WidthPx,
                        HeightPx = baseMon.HeightPx,

                        // а вот мм — разные (физические)
                        WidthMm = wm.WidthMm,
                        HeightMm = wm.HeightMm
                    });
                }

                return wmiList;
            }
        }

        return list;

    }

    private static List<(string dev, string friendly)> EnumerateAttachedDisplays()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICE();
            adapter.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

            if (!EnumDisplayDevices(null, i, ref adapter, 0))
                break;

            // игнорим “зеркальные драйверы”
            if ((adapter.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                continue;

            // для каждого адаптера — мониторы
            for (uint j = 0; ; j++)
            {
                var mon = new DISPLAY_DEVICE();
                mon.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

                if (!EnumDisplayDevices(adapter.DeviceName, j, ref mon, 0))
                    break;

                if ((mon.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                    continue;

                if ((mon.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                    continue;

                var baseDev = NormalizeGdiDeviceName(mon.DeviceName);
                if (string.IsNullOrWhiteSpace(baseDev))
                    continue;

                var friendly = string.IsNullOrWhiteSpace(mon.DeviceString) ? "" : mon.DeviceString.Trim();

                // по одному friendly на dev
                if (!map.ContainsKey(baseDev))
                    map[baseDev] = friendly;
            }
        }

        return map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static string NormalizeGdiDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "";
        var s = deviceName.Trim();

        // иногда приходит "\\.\DISPLAY1\Monitor0" — нам нужно "\\.\DISPLAY1"
        var idx = s.IndexOf("\\Monitor", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return s.Substring(0, idx);

        return s;
    }

    // маленький helper для DistinctBy на старом target
    private static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> src, Func<T, TKey> key, IEqualityComparer<TKey> cmp)
    {
        var seen = new HashSet<TKey>(cmp);
        foreach (var x in src)
            if (seen.Add(key(x)))
                yield return x;
    }

    /// <summary>
    /// Возвращает монитор по индексу config.write_desktop.
    /// Если индекс невалидный — fallback на primary, иначе на первый.
    /// </summary>
    public static DisplayMonitor GetSelected(int selectedIndex)
    {
        var mons = GetMonitors();
        if (mons.Count == 0)
            return new DisplayMonitor { Index = 0, DeviceName = "UNKNOWN" };

        if (selectedIndex >= 0 && selectedIndex < mons.Count)
            return mons[selectedIndex];

        return mons.FirstOrDefault(m => m.IsPrimary) ?? mons[0];
    }

    public static (int wPx, int hPx, int wMm, int hMm) GetSelectedMetrics(int selectedIndex)
    {
        var m = GetSelected(selectedIndex);

        // Пиксели обязаны быть >0 (мы их добываем через системные функции + fallback)
        if (m.WidthPx <= 0 || m.HeightPx <= 0)
            throw new InvalidOperationException("Не удалось получить размеры экрана в пикселях системными средствами.");

        return (m.WidthPx, m.HeightPx, m.WidthMm, m.HeightMm);
    }

    private static bool TryGetMmFromMonitorDpi(IntPtr hMon, int wPx, int hPx, out int wMm, out int hMm)
    {
        wMm = 0; hMm = 0;

        try
        {
            // 2 = MDT_RAW_DPI (самое близкое к физике), если не даст — попробуем EFFECTIVE
            if (GetDpiForMonitor(hMon, MONITOR_DPI_TYPE.MDT_RAW_DPI, out uint dpiX, out uint dpiY) != 0 ||
                dpiX == 0 || dpiY == 0)
            {
                if (GetDpiForMonitor(hMon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY) != 0 ||
                    dpiX == 0 || dpiY == 0)
                    return false;
            }

            wMm = (int)Math.Round(wPx / (double)dpiX * 25.4);
            hMm = (int)Math.Round(hPx / (double)dpiY * 25.4);

            return wMm > 0 && hMm > 0;
        }
        catch
        {
            return false;
        }
    }

    private static int ExtractDisplayNumber(string dev)
    {
        // dev обычно "\\.\DISPLAY1"
        if (string.IsNullOrWhiteSpace(dev)) return int.MaxValue;
        var i = dev.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return int.MaxValue;

        var s = dev[(i + "DISPLAY".Length)..];
        return int.TryParse(s, out var n) ? n : int.MaxValue;
    }

    private static Rect RectFrom(RECT r)
        => new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);

        private sealed class WmiMonitorInfo
    {
        public string InstanceName { get; init; } = "";
        public string DisplayText { get; init; } = "";
        public int WidthMm { get; init; }
        public int HeightMm { get; init; }
    }

    private static List<WmiMonitorInfo> TryGetWmiActiveMonitors()
    {
        try
        {
            // InstanceName -> (wMm,hMm)
            var sizes = new Dictionary<string, (int wMm, int hMm)>(StringComparer.OrdinalIgnoreCase);
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var s = new ManagementObjectSearcher(
                       @"root\wmi",
                       "SELECT InstanceName, Active, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams"))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    var inst = (mo["InstanceName"] as string) ?? "";
                    if (string.IsNullOrWhiteSpace(inst)) continue;

                    bool isActive = true;
                    if (mo.Properties["Active"] != null && mo["Active"] != null)
                    {
                        // иногда тип бывает UInt16/Boolean — приводим безопасно
                        isActive = Convert.ToBoolean(mo["Active"]);
                    }

                    if (isActive) active.Add(inst);

                    int wCm = 0, hCm = 0;
                    if (mo["MaxHorizontalImageSize"] != null) wCm = Convert.ToInt32(mo["MaxHorizontalImageSize"]);
                    if (mo["MaxVerticalImageSize"] != null) hCm = Convert.ToInt32(mo["MaxVerticalImageSize"]);

                    var wMm = wCm > 0 ? wCm * 10 : 0;
                    var hMm = hCm > 0 ? hCm * 10 : 0;

                    sizes[inst] = (wMm, hMm);
                }
            }

            // InstanceName -> текст (Friendly + Serial)
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var s = new ManagementObjectSearcher(
                       @"root\wmi",
                       "SELECT InstanceName, UserFriendlyName, SerialNumberID, ManufacturerName, ProductCodeID FROM WmiMonitorID"))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    var inst = (mo["InstanceName"] as string) ?? "";
                    if (string.IsNullOrWhiteSpace(inst)) continue;

                    var friendly = DecodeUShortString(mo["UserFriendlyName"]);
                    var serial = DecodeUShortString(mo["SerialNumberID"]);
                    var mfg = DecodeUShortString(mo["ManufacturerName"]);
                    var prod = DecodeUShortString(mo["ProductCodeID"]);

                    string title = !string.IsNullOrWhiteSpace(friendly)
                        ? friendly
                        : $"{mfg} {prod}".Trim();

                    if (string.IsNullOrWhiteSpace(title))
                        title = ShortInstance(inst);

                    if (!string.IsNullOrWhiteSpace(serial))
                        title += $" • S/N {serial}";

                    names[inst] = title;
                }
            }

            // Если Active не отдали — считаем активными всех, по которым вообще есть размеры
            if (active.Count == 0)
                foreach (var k in sizes.Keys) active.Add(k);

            var list = new List<WmiMonitorInfo>();

            foreach (var inst in active)
            {
                sizes.TryGetValue(inst, out var sz);
                names.TryGetValue(inst, out var title);

                list.Add(new WmiMonitorInfo
                {
                    InstanceName = inst,
                    DisplayText = string.IsNullOrWhiteSpace(title) ? ShortInstance(inst) : title!,
                    WidthMm = sz.wMm,
                    HeightMm = sz.hMm
                });
            }

            // Стабильный порядок (по названию)
            list.Sort((a, b) => string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase));
            return list;
        }
        catch
        {
            return new List<WmiMonitorInfo>();
        }
    }

    private static string DecodeUShortString(object? v)
    {
        if (v == null) return "";

        // WMI часто отдаёт ushort[] (UInt16[])
        if (v is ushort[] u16)
        {
            var chars = u16.TakeWhile(x => x != 0).Select(x => (char)x).ToArray();
            return new string(chars).Trim();
        }

        if (v is Array arr)
        {
            var tmp = new List<char>();
            foreach (var o in arr)
            {
                if (o == null) continue;
                var n = Convert.ToUInt16(o);
                if (n == 0) break;
                tmp.Add((char)n);
            }
            return new string(tmp.ToArray()).Trim();
        }

        return v.ToString() ?? "";
    }

    private static string ShortInstance(string inst)
    {
        // Пример InstanceName: DISPLAY\DEL40A1\5&... -> берём DEL40A1
        try
        {
            var parts = inst.Split('\\');
            if (parts.Length >= 2) return parts[1];
        }
        catch { }
        return inst;
    }

    // ===== WinAPI =====
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    private const int MONITORINFOF_PRIMARY = 1;
    private const int ENUM_CURRENT_SETTINGS = -1;

    // GetDeviceCaps indexes
    private const int HORZSIZE = 4;     // mm
    private const int VERTSIZE = 6;     // mm
    private const int HORZRES = 8;      // px
    private const int VERTRES = 10;     // px
    private const int DESKTOPVERTRES = 117; // px (real)
    private const int DESKTOPHORZRES = 118; // px (real)

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    private enum MONITOR_DPI_TYPE
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT = MDT_EFFECTIVE_DPI
    }

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;

        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;

        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;

        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;

        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
