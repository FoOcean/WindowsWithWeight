using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WindowsWithWeight.Testing;

/// <summary>
/// A colourful, ordinary captioned window owned by this process. Marked with
/// <see cref="WindowFilter.TestProp"/> so the self-test engine may move it (and the live engine never does).
/// Shown without activation so tests never steal keyboard focus from the user.
/// </summary>
public sealed class TestWindow : Form
{
    private readonly Color color;
    private readonly string glyphName;
    private readonly bool topMost;
    private readonly bool toolWindow;
    private Bitmap? glyph;
    public string Label { get; }

    public TestWindow(string label, string glyphName, Color color, bool topMost, bool toolWindow = false)
    {
        Label = label;
        this.glyphName = glyphName;
        this.color = color;
        this.topMost = topMost;
        this.toolWindow = toolWindow;
        Text = label;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = color;
        DoubleBuffered = true;
        Location = new Point(-32000, -32000);
        Size = new Size(300, 200);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (topMost) cp.ExStyle |= (int)Native.WS_EX_TOPMOST;
            if (toolWindow) cp.ExStyle |= (int)Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetProp(Handle, WindowFilter.TestProp, new IntPtr(1));
        // Windows 11: colour the title bar to match the window (ignored on Windows 10).
        Native.SetDwmInt(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Native.SetDwmInt(Handle, Native.DWMWA_CAPTION_COLOR, Native.ColorRef(Shade(color, 0.78f)));
        Native.SetDwmInt(Handle, Native.DWMWA_BORDER_COLOR, Native.ColorRef(Shade(color, 0.9f)));
        Native.SetDwmInt(Handle, Native.DWMWA_TEXT_COLOR, Native.ColorRef(Color.White));
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.RemoveProp(Handle, WindowFilter.TestProp);
        base.OnHandleDestroyed(e);
    }

    private static Color Shade(Color c, float f) => Color.FromArgb(255, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rc = ClientRectangle;
        if (rc.Width <= 0 || rc.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (var bg = new LinearGradientBrush(rc, Shade(color, 1.0f), Shade(color, 0.82f), LinearGradientMode.Vertical))
            g.FillRectangle(bg, rc);

        int side = Math.Min(rc.Width, rc.Height);
        int glyphSize = (int)(side * 0.38);
        if (glyph == null || glyph.Width != glyphSize)
        {
            glyph?.Dispose();
            glyph = glyphSize > 4 ? Assets.Tinted(glyphName, Color.White, glyphSize, 0.95f) : null;
        }
        float textH = Math.Max(12f, side * 0.13f);
        using var title = new Font("Segoe UI Semibold", textH, FontStyle.Regular, GraphicsUnit.Pixel);
        using var sub = new Font("Segoe UI", textH * 0.8f, FontStyle.Regular, GraphicsUnit.Pixel);
        float blockH = glyphSize + textH * 0.6f + title.Height + sub.Height;
        float y = rc.Top + (rc.Height - blockH) / 2f;
        if (glyph != null) g.DrawImage(glyph, rc.Left + (rc.Width - glyphSize) / 2, (int)y);
        y += glyphSize + textH * 0.6f;
        using var fmt = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(Label, title, Brushes.White, new RectangleF(rc.Left, y, rc.Width, title.Height + 4), fmt);
        y += title.Height;
        // mass is proportional to visible area; show it relative to a 100x100 DIP window
        double scale = Monitors.ScaleAt(new RectD(Left, Top, Right, Bottom));
        double mass = Width * Height / (scale * scale) / 10000.0;
        using var subBrush = new SolidBrush(Color.FromArgb(205, 255, 255, 255));
        g.DrawString($"mass {mass:0.0}", sub, subBrush, new RectangleF(rc.Left, y, rc.Width, sub.Height + 4), fmt);
    }

    /// <summary>Shows the window (no activation) with its *visible* frame at the given physical-pixel rectangle.</summary>
    public void ShowAtVisible(RectD visible)
    {
        if (!Visible)
        {
            // start roughly in place (same monitor / DPI), then correct for the exact invisible borders
            Bounds = new Rectangle((int)visible.L - 8, (int)visible.T - 1, (int)visible.W + 16, (int)visible.H + 9);
            Show();
        }
        PlaceVisible(visible);
        // DWM may report the final frame only after the first show: correct once more.
        PlaceVisible(visible);
        Invalidate();
    }

    public void PlaceVisible(RectD v)
    {
        if (!WindowGeometry.TryGet(Handle, out var g)) return;
        int il = g.Visible.Left - g.Window.Left, it = g.Visible.Top - g.Window.Top;
        int ir = g.Window.Right - g.Visible.Right, ib = g.Window.Bottom - g.Visible.Bottom;
        var r = v.ToRECT();
        Native.SetWindowPos(Handle, IntPtr.Zero, r.Left - il, r.Top - it, r.Width + il + ir, r.Height + it + ib,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) glyph?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Borderless, click-through backdrop for recording a clean demo (never simulated).</summary>
public sealed class StageWindow : Form
{
    private string caption = "";
    private string hint = "";
    private Bitmap? logo;

    public StageWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(14, 17, 30);
        Location = new Point(-32000, -32000);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetProp(Handle, WindowFilter.IgnoreProp, new IntPtr(1));
        Native.SetDwmInt(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 1); // DWMWCP_DONOTROUND
    }

    public void SetCaption(string text, string hintText = "")
    {
        caption = text;
        hint = hintText;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rc = ClientRectangle;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var bg = new LinearGradientBrush(rc, Color.FromArgb(24, 28, 52), Color.FromArgb(9, 11, 20), LinearGradientMode.Vertical))
            g.FillRectangle(bg, rc);
        // faint dot grid
        float step = rc.Height / 18f;
        using (var dot = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
            for (float y = step / 2; y < rc.Height; y += step)
            for (float x = step / 2; x < rc.Width; x += step)
                g.FillEllipse(dot, x - 1.2f, y - 1.2f, 2.4f, 2.4f);

        float u = rc.Height / 720f;
        int logoSize = (int)(26 * u);
        if (logo == null || logo.Width != logoSize) { logo?.Dispose(); logo = Assets.Tinted("weight", Color.FromArgb(150, 160, 255), logoSize); }
        g.DrawImage(logo, (int)(26 * u), (int)(22 * u));
        using (var f = new Font("Segoe UI Semibold", 20 * u, GraphicsUnit.Pixel))
        using (var b = new SolidBrush(Color.FromArgb(190, 205, 215, 255)))
            g.DrawString("Windows with Weight", f, b, 60 * u, 22 * u);

        if (caption.Length > 0)
        {
            using var f = new Font("Segoe UI Semibold", 24 * u, GraphicsUnit.Pixel);
            using var fh = new Font("Segoe UI", 17 * u, GraphicsUnit.Pixel);
            var size = g.MeasureString(caption, f);
            var hsize = hint.Length > 0 ? g.MeasureString(hint, fh) : SizeF.Empty;
            float w = Math.Max(size.Width, hsize.Width) + 40 * u;
            float h = size.Height + (hint.Length > 0 ? hsize.Height : 0) + 18 * u;
            var pill = new RectangleF(rc.Width - w - 26 * u, 18 * u, w, h);
            using (var path = RoundRect(pill, 14 * u))
            using (var pb = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
            using (var fill = new SolidBrush(Color.FromArgb(46, 120, 130, 255)))
            {
                g.FillPath(fill, path);
                using var pen = new Pen(Color.FromArgb(70, 170, 180, 255), 1.5f * u);
                g.DrawPath(pen, path);
            }
            g.DrawString(caption, f, Brushes.White, pill.X + 20 * u, pill.Y + 8 * u);
            if (hint.Length > 0)
                using (var hb = new SolidBrush(Color.FromArgb(185, 210, 215, 255)))
                    g.DrawString(hint, fh, hb, pill.X + 20 * u, pill.Y + 8 * u + size.Height);
        }
    }

    private static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) logo?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Per-pixel-alpha, click-through "hand" shown during scripted throws so a video reads like a real drag.
/// It is only an image: it never moves the real cursor or sends input.</summary>
public sealed class HandOverlay : Form
{
    private readonly Bitmap image;
    public int Size2 { get; }

    public HandOverlay(int size)
    {
        Size2 = size;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        Location = new Point(-32000, -32000);
        image = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.Transparent);
        int inner = (int)(size * 0.86);
        using (var shadow = Assets.Tinted("hand", Color.Black, inner, 0.45f))
            g.DrawImage(shadow, (int)(size * 0.07), (int)(size * 0.08));
        using (var hand = Assets.Tinted("hand", Color.White, inner))
            g.DrawImage(hand, 0, 0);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                                Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetProp(Handle, WindowFilter.IgnoreProp, new IntPtr(1));
    }

    /// <summary>Places the fingertip (top centre of the glyph) at (x, y) with the given opacity.</summary>
    public void Present(int x, int y, byte alpha)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr hbmp = image.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Native.SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { cx = image.Width, cy = image.Height };
            var src = new POINT();
            // Fingertip of hand.png is at (0.50, 0.04) of the glyph, which is drawn at 0.86 of the image size.
            var dst = new POINT { X = x - (int)(image.Width * 0.43), Y = y - (int)(image.Height * 0.035) };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(hbmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) image.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Drives the real drag code path without touching the mouse: calls the MOVESIZESTART/END handlers the
/// WinEvent hook would call, and moves the window with SetWindowPos in between, so the real
/// LOCATIONCHANGE hook and the simulation thread sample it exactly as during a user drag.
/// </summary>
public static class ScriptedDrag
{
    /// <param name="windup">Pull back this fraction of the throw distance first (looks like a real flick).</param>
    public static ReleaseResult? Throw(UiThread ui, DragWatcher watcher, IntPtr hwnd, double dx, double dy, double seconds,
        PreciseTimer timer, double windup = 0, HandOverlay? hand = null, double holdBefore = 0, bool beginDrag = true)
    {
        Native.GetWindowRect(hwnd, out var r0);
        var grab = new POINT { X = r0.Left + r0.Width / 3, Y = r0.Top + 18 };
        if (hand != null) ui.Invoke(() => { if (!hand.Visible) hand.Show(); hand.Present(grab.X, grab.Y, 255); });
        if (holdBefore > 0) timer.Sleep(holdBefore);
        if (beginDrag) ui.Invoke(() => watcher.HandleStart(hwnd));
        double x0 = r0.Left, y0 = r0.Top;

        void MoveTo(double x, double y)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            if (hand != null)
            {
                int hx = grab.X + (int)Math.Round(x - r0.Left), hy = grab.Y + (int)Math.Round(y - r0.Top);
                ui.Invoke(() => hand.Present(hx, hy, 255));
            }
        }

        if (windup > 0)
        {
            double wsec = 0.16;
            var sw0 = Stopwatch.StartNew();
            while (sw0.Elapsed.TotalSeconds < wsec)
            {
                double f = sw0.Elapsed.TotalSeconds / wsec;
                double e = 1 - (1 - f) * (1 - f);
                MoveTo(r0.Left - dx * windup * e, r0.Top - dy * windup * e);
                timer.Sleep(0.007);
            }
            x0 = r0.Left - dx * windup; y0 = r0.Top - dy * windup;
            MoveTo(x0, y0);
            timer.Sleep(0.04);
            dx *= 1 + windup; dy *= 1 + windup;
        }

        var sw = Stopwatch.StartNew();
        while (true)
        {
            double t = sw.Elapsed.TotalSeconds;
            if (t >= seconds) break;
            double f = t / seconds;
            MoveTo(x0 + dx * f * f, y0 + dy * f * f); // accelerating, like a flick
            timer.Sleep(0.007);
        }
        MoveTo(x0 + dx, y0 + dy);
        var result = ui.Invoke(() => watcher.HandleEnd(hwnd));
        if (hand != null)
        {
            int fx = grab.X + (int)Math.Round(x0 + dx - r0.Left), fy = grab.Y + (int)Math.Round(y0 + dy - r0.Top);
            for (int a = 255; a > 0; a -= 32)
            {
                byte alpha = (byte)a;
                ui.Invoke(() => hand.Present(fx, fy, alpha));
                timer.Sleep(0.016);
            }
            ui.Invoke(() => hand.Present(-32000, -32000, 0));
        }
        return result;
    }

    /// <summary>Expected release speed of <see cref="Throw"/> (no windup) for the estimator's averaging window.</summary>
    public static double ExpectedSpeed(double distance, double seconds, double window)
    {
        double w = Math.Min(window, seconds);
        double a = (seconds - w) / seconds;
        return distance * (1 - a * a) / w;
    }
}
