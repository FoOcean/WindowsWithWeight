using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WindowsWithWeight;

/// <summary>Window rect plus the visible frame DWM draws (no invisible resize borders).</summary>
public readonly struct WinGeom
{
    public readonly RECT Window;
    public readonly RECT Visible;
    public WinGeom(RECT window, RECT visible) { Window = window; Visible = visible; }
    public int InsetL => Visible.Left - Window.Left;
    public int InsetT => Visible.Top - Window.Top;
    public RectD VisibleD => Visible.ToRectD();
}

public static class WindowGeometry
{
    public static bool TryGet(IntPtr hwnd, out WinGeom g)
    {
        g = default;
        if (!Native.GetWindowRect(hwnd, out var wr)) return false;
        RECT vis = wr;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dv, Marshal.SizeOf<RECT>()) == 0
            && dv.Width > 0 && dv.Height > 0
            // sanity: the visible frame must sit inside (or on) the window rect
            && dv.Left >= wr.Left - 1 && dv.Top >= wr.Top - 1 && dv.Right <= wr.Right + 1 && dv.Bottom <= wr.Bottom + 1)
        {
            vis = dv;
        }
        g = new WinGeom(wr, vis);
        return true;
    }
}

/// <summary>Decides which top-level windows physics may touch.</summary>
public static class WindowFilter
{
    /// <summary>Window property marking this app's own self-test / demo windows.</summary>
    public const string TestProp = "WindowsWithWeight.TestWindow";
    /// <summary>Window property marking this app's helper windows (stage, overlays) that must never be simulated.</summary>
    public const string IgnoreProp = "WindowsWithWeight.Ignore";

    private static readonly uint OwnPid = (uint)Environment.ProcessId;
    private static readonly ConcurrentDictionary<uint, bool> MovableCache = new();
    private static readonly Lazy<int> OwnIntegrity = new(() => GetIntegrity(Native.GetCurrentProcess()));

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "DV2ControlHost", "Button",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow", "Shell_InputSwitchTopLevelWindow", "MultitaskingViewFrame", "ForegroundStaging",
        "TaskListThumbnailWnd", "Windows.Internal.Shell.TabProxyWindow", "Shell_LightDismissOverlay",
        "Xaml_WindowedPopupClass", "EdgeUiInputTopWndClass", "ApplicationManager_ImmersiveShellWindow",
        "#32768", "#32769", "tooltips_class32", "SysShadow", "Ghost", "Internet Explorer_Hidden",
        "ThumbnailDeviceHelperWnd", "Shell_Dim", "WindowsDashboard",
    };

    /// <summary>Returns null when the window is eligible, otherwise a short human-readable reason.</summary>
    /// <param name="testMode">Self-test/demo: only this app's own test windows are eligible.
    /// Live mode: this app's windows are never eligible.</param>
    public static string? Reject(IntPtr hwnd, bool testMode)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return "gone";
        if (!Native.IsWindowVisible(hwnd)) return "hidden";
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return "child";
        if (hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return "shell";
        string cls = Native.ClassName(hwnd);
        if (ShellClasses.Contains(cls)) return "shell";
        if (Native.GetProp(hwnd, IgnoreProp) != IntPtr.Zero) return "helper";

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        bool isTest = Native.GetProp(hwnd, TestProp) != IntPtr.Zero;
        if (testMode)
        {
            if (!isTest || pid != OwnPid) return "not a test window";
        }
        else if (pid == OwnPid || isTest)
        {
            return "own window";
        }

        long style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        long ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((style & Native.WS_CHILD) != 0) return "child";
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return "tool window";
        if ((ex & Native.WS_EX_NOACTIVATE) != 0) return "no-activate overlay";
        if ((ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0) return "click-through overlay";
        if ((style & Native.WS_CAPTION) != Native.WS_CAPTION && (style & Native.WS_THICKFRAME) == 0) return "no frame";
        if (Native.IsIconic(hwnd)) return "minimized";
        if (Native.IsZoomed(hwnd)) return "maximized";
        if (Native.IsCloaked(hwnd)) return "cloaked";
        if (Native.IsHungAppWindow(hwnd)) return "not responding";
        if (!WindowGeometry.TryGet(hwnd, out var g)) return "no geometry";
        if (g.Visible.Width < 60 || g.Visible.Height < 30) return "too small";

        var vis = g.Visible;
        var mon = Native.MonitorFromRect(ref vis, Native.MONITOR_DEFAULTTONEAREST);
        var md = Monitors.Describe(mon);
        if (md != null && g.VisibleD.Contains(md.Bounds, 0.5)) return "full-screen";

        if (!testMode && !CanMoveProcess(pid)) return "elevated";
        return null;
    }

    /// <summary>
    /// UIPI blocks moving windows of processes with a higher integrity level (e.g. elevated apps).
    /// Returns false for those, and for processes we cannot even query (protected/system).
    /// </summary>
    public static bool CanMoveProcess(uint pid)
    {
        if (pid == OwnPid) return true;
        return MovableCache.GetOrAdd(pid, p =>
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p);
            if (h == IntPtr.Zero) return false;
            try
            {
                int il = GetIntegrity(h);
                return il >= 0 && il <= OwnIntegrity.Value;
            }
            finally { Native.CloseHandle(h); }
        });
    }

    public static void ForgetProcess(uint pid) => MovableCache.TryRemove(pid, out _);

    public static int OwnIntegrityLevel => OwnIntegrity.Value;

    /// <summary>Mandatory integrity RID (0x2000 medium, 0x3000 high, 0x4000 system) or -1.</summary>
    public static int GetIntegrity(IntPtr process)
    {
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out IntPtr token)) return -1;
        try
        {
            Native.GetTokenInformation(token, Native.TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
            if (len <= 0) return -1;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buf, len, out _)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf); // TOKEN_MANDATORY_LABEL.Label.Sid
                int count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                if (count <= 0) return -1;
                return Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Native.CloseHandle(token); }
    }

    /// <summary>All top-level windows in Z order (top first).</summary>
    public static List<IntPtr> TopLevelWindows()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
