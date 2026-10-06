namespace WindowsWithWeight;

public sealed record MonitorData(IntPtr Handle, string Device, RectD Bounds, RectD Work, bool Primary, double Scale, int RefreshHz);

public static class Monitors
{
    public static List<MonitorData> All(bool withRefreshRate = false)
    {
        var handles = new List<IntPtr>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            handles.Add(h);
            return true;
        }, IntPtr.Zero);
        var list = new List<MonitorData>();
        foreach (var h in handles)
        {
            var m = Describe(h, withRefreshRate);
            if (m != null) list.Add(m);
        }
        return list;
    }

    public static MonitorData? Describe(IntPtr h, bool withRefreshRate = false)
    {
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(h, ref mi)) return null;
        double scale = 1.0;
        if (Native.GetDpiForMonitor(h, 0, out uint dx, out _) == 0 && dx > 0) scale = dx / 96.0;
        int hz = 60;
        if (withRefreshRate)
        {
            var dm = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (Native.EnumDisplaySettings(mi.szDevice, -1, ref dm) && dm.dmDisplayFrequency > 1) hz = dm.dmDisplayFrequency;
        }
        return new MonitorData(h, mi.szDevice, mi.rcMonitor.ToRectD(), mi.rcWork.ToRectD(),
            (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, scale, hz);
    }

    public static MonitorData Primary(bool withRefreshRate = false)
    {
        var all = All(withRefreshRate);
        return all.FirstOrDefault(m => m.Primary) ?? all.First();
    }

    public static MonitorData FromPoint(int x, int y)
    {
        var h = Native.MonitorFromPoint(new POINT { X = x, Y = y }, Native.MONITOR_DEFAULTTONEAREST);
        return Describe(h) ?? Primary();
    }

    public static MonitorData FromRect(in RectD r)
    {
        var rc = r.ToRECT();
        var h = Native.MonitorFromRect(ref rc, Native.MONITOR_DEFAULTTONEAREST);
        return Describe(h) ?? Primary();
    }

    /// <summary>DPI scale (1.0 = 96 dpi) of the monitor that holds most of <paramref name="r"/>.</summary>
    public static double ScaleAt(in RectD r)
    {
        var rc = r.ToRECT();
        var h = Native.MonitorFromRect(ref rc, Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetDpiForMonitor(h, 0, out uint dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1.0;
    }

    /// <summary>Highest refresh rate among the monitors (used to pace the simulation when DwmFlush does not block).</summary>
    public static int MaxRefreshHz()
    {
        try { return Math.Clamp(All(true).Max(m => m.RefreshHz), 30, 500); }
        catch { return 60; }
    }
}
