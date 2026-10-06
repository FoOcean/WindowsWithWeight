using System.Drawing;
using WindowsWithWeight.Physics;

namespace WindowsWithWeight;

/// <summary>Message-only window that receives WM_HOTKEY.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly List<int> ids = new();
    public event Action<int>? Pressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams { Parent = Native.HWND_MESSAGE, Caption = "WindowsWithWeight.Hotkeys" });
    }

    public bool Register(int id, uint modifiers, Keys key)
    {
        bool ok = Native.RegisterHotKey(Handle, id, modifiers | Native.MOD_NOREPEAT, (uint)key);
        if (ok) ids.Add(id);
        return ok;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) Pressed?.Invoke((int)m.WParam);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in ids) Native.UnregisterHotKey(Handle, id);
        ids.Clear();
        DestroyHandle();
    }
}

/// <summary>The normal mode: a tray icon, global hotkeys and the drag watcher over every eligible window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const int HotkeyGravity = 1, HotkeyRestore = 2;

    private readonly Engine engine;
    private readonly DragWatcher watcher;
    private readonly HotkeyWindow hotkeys;
    private readonly NotifyIcon tray;
    private readonly Control invoker;
    private readonly ToolStripMenuItem miEnabled, miGravity, miRestore, miLight, miNormal, miHeavy;
    private readonly EventWaitHandle exitSignal;
    private readonly RegisteredWaitHandle exitWait;
    private IntPtr iconHandle, gravityIconHandle;
    private bool exiting;

    public TrayApp(Options options)
    {
        invoker = new Control();
        invoker.CreateControl();
        _ = invoker.Handle;

        engine = new Engine(new LiveWindowSource(), new LiveArena(), PhysicsSettings.ByName(options.Preset));
        engine.Enabled = !options.StartDisabled;
        engine.StateChanged += () => { if (!exiting && invoker.IsHandleCreated) invoker.BeginInvoke(UpdateUi); };
        engine.Start();

        watcher = new DragWatcher(engine);
        bool hooked = watcher.Install();

        hotkeys = new HotkeyWindow();
        bool g = hotkeys.Register(HotkeyGravity, Native.MOD_CONTROL | Native.MOD_ALT, Keys.G);
        bool r = hotkeys.Register(HotkeyRestore, Native.MOD_CONTROL | Native.MOD_ALT, Keys.R);
        hotkeys.Pressed += id =>
        {
            if (id == HotkeyGravity) engine.ToggleGravity();
            else if (id == HotkeyRestore) engine.RestoreAll();
        };

        miEnabled = new ToolStripMenuItem("Enabled", null, (_, _) => engine.Enabled = !engine.Enabled);
        miGravity = new ToolStripMenuItem("Gravity mode", null, (_, _) => engine.ToggleGravity()) { ShortcutKeyDisplayString = "Ctrl+Alt+G" };
        miRestore = new ToolStripMenuItem("Restore positions", null, (_, _) => engine.RestoreAll()) { ShortcutKeyDisplayString = "Ctrl+Alt+R" };
        miLight = new ToolStripMenuItem("Light  (floaty, bouncy)", null, (_, _) => engine.Settings = PhysicsSettings.Light);
        miNormal = new ToolStripMenuItem("Normal", null, (_, _) => engine.Settings = PhysicsSettings.Normal);
        miHeavy = new ToolStripMenuItem("Heavy  (short slides, dull bounces)", null, (_, _) => engine.Settings = PhysicsSettings.Heavy);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Windows with Weight") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miEnabled);
        menu.Items.Add(miGravity);
        menu.Items.Add(miRestore);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Feel") { Enabled = false });
        menu.Items.Add(miLight);
        menu.Items.Add(miNormal);
        menu.Items.Add(miHeavy);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Shutdown()));
        menu.Opening += (_, _) => UpdateUi();

        tray = new NotifyIcon { ContextMenuStrip = menu, Visible = false };
        BuildIcons();
        UpdateUi();
        tray.Visible = true;

        var problems = new List<string>();
        if (!hooked) problems.Add("Could not hook window drags (SetWinEventHook failed).");
        if (!g) problems.Add("Ctrl+Alt+G is taken by another app: use the tray menu for gravity.");
        if (!r) problems.Add("Ctrl+Alt+R is taken by another app: use the tray menu to restore.");
        if (problems.Count > 0) tray.ShowBalloonTip(6000, "Windows with Weight", string.Join("\n", problems), ToolTipIcon.Warning);

        // `WindowsWithWeight.exe --exit` signals this event to close a running instance cleanly.
        exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) =>
        {
            if (invoker.IsHandleCreated) invoker.BeginInvoke(Shutdown);
        }, null, Timeout.Infinite, true);

        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => invoker.BeginInvoke(Shutdown);
    }

    private void BuildIcons()
    {
        uint dpi = Native.GetDpiForSystem();
        int size = Math.Max(16, Native.GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi));
        var normal = Assets.TaskbarIsLight() ? Color.FromArgb(32, 32, 32) : Color.White;
        using (var bmp = Assets.Tinted("weight", normal, size)) iconHandle = bmp.GetHicon();
        using (var bmp = Assets.Tinted("weight", Color.FromArgb(255, 176, 32), size)) gravityIconHandle = bmp.GetHicon();
    }

    private void UpdateUi()
    {
        if (exiting) return;
        bool on = engine.Enabled, grav = engine.GravityOn;
        miEnabled.Checked = on;
        miGravity.Checked = grav;
        miGravity.Enabled = on;
        int saved = engine.SavedPositionCount;
        miRestore.Text = saved > 0 ? $"Restore positions ({saved})" : "Restore positions";
        var s = engine.Settings.Name;
        miLight.Checked = s == "Light";
        miNormal.Checked = s == "Normal";
        miHeavy.Checked = s == "Heavy";
        tray.Icon = Icon.FromHandle(grav ? gravityIconHandle : iconHandle);
        tray.Text = !on ? "Windows with Weight (paused)"
            : grav ? "Windows with Weight: gravity on (Ctrl+Alt+G to restore)"
            : "Windows with Weight: throw a window! Ctrl+Alt+G = gravity";
    }

    private void Shutdown()
    {
        if (exiting) return;
        exiting = true;
        exitWait.Unregister(null);
        watcher.Dispose();
        hotkeys.Dispose();
        engine.Shutdown(); // puts gravity-mode windows back where they were
        tray.Visible = false;
        tray.Dispose();
        if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
        if (gravityIconHandle != IntPtr.Zero) Native.DestroyIcon(gravityIconHandle);
        exitSignal.Dispose();
        ExitThread();
    }
}
