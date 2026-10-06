using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WindowsWithWeight;

/// <summary>Embedded glyphs (Fluent UI System Icons rendered to PNG, see tools/make-icons.ps1). Tinted at runtime, never drawn by hand.</summary>
public static class Assets
{
    public static Bitmap Load(string name)
    {
        using var s = typeof(Assets).Assembly.GetManifestResourceStream($"Assets.{name}.png")
                      ?? throw new FileNotFoundException("Missing embedded asset " + name);
        using var tmp = new Bitmap(s);
        return new Bitmap(tmp); // detach from the stream
    }

    /// <summary>Glyph recoloured to <paramref name="tint"/> (alpha kept), resized to a square of <paramref name="size"/> px.</summary>
    public static Bitmap Tinted(string name, Color tint, int size, float opacity = 1f)
    {
        using var src = Load(name);
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        var m = new ColorMatrix(new[]
        {
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, opacity, 0 },
            new float[] { tint.R / 255f, tint.G / 255f, tint.B / 255f, 0, 1 },
        });
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(m);
        g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    public static bool TaskbarIsLight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }
}
