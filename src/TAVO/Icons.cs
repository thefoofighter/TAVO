using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TAVO;

internal static class Icons
{
    public static readonly int[] Sizes = { 16, 20, 24, 30, 32, 36, 40, 48, 64, 256 };

    /// <summary>Badge colors, assigned by version order inside a product group. White text is readable on all.</summary>
    public static readonly Color[] Palette =
    {
        Color.FromArgb(0xC6, 0x28, 0x28), Color.FromArgb(0x15, 0x65, 0xC0), Color.FromArgb(0x2E, 0x7D, 0x32),
        Color.FromArgb(0x6A, 0x1B, 0x9A), Color.FromArgb(0xE6, 0x51, 0x00), Color.FromArgb(0x00, 0x83, 0x8F),
        Color.FromArgb(0xAD, 0x14, 0x57), Color.FromArgb(0x4E, 0x34, 0x2E),
    };

    /// <summary>Builds a multi-size badged .ico from an icon resource. Returns null if any size cannot be extracted.</summary>
    public static byte[]? BuildBadgedIco(string source, int index, string label, Color color)
    {
        var frames = new List<Bitmap>();
        try
        {
            foreach (int s in Sizes)
            {
                using var src = Extract(source, index, s);
                if (src == null) return null;
                frames.Add(Badge(src, s, label, color));
            }
            return BuildIco(frames);
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
        }
    }

    public static Bitmap? Extract(string path, int index, int size)
    {
        var h = new IntPtr[1];
        var ids = new uint[1];
        uint n = Native.PrivateExtractIconsW(path, index, size, size, h, ids, 1, 0);
        if (n == 0 || n == 0xFFFFFFFF || h[0] == IntPtr.Zero) return null;
        try { return FromHIcon(h[0]); }
        finally { Native.DestroyIcon(h[0]); }
    }

    static Bitmap? FromHIcon(IntPtr hIcon)
    {
        if (!Native.GetIconInfo(hIcon, out var ii)) return null;
        try
        {
            if (ii.hbmColor == IntPtr.Zero) return null; // monochrome icons are not supported
            Native.GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bm);
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            var px = Bits32(ii.hbmColor, w, h);
            if (px == null) return null;
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha && ii.hbmMask != IntPtr.Zero)
            {
                // Old 24-bit icon: build alpha from the AND mask (white = transparent).
                var mask = Bits32(ii.hbmMask, w, h);
                if (mask != null)
                    for (int i = 0; i < w * h; i++) px[i * 4 + 3] = mask[i * 4] == 0 ? (byte)255 : (byte)0;
            }
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
            bmp.UnlockBits(data);
            return bmp;
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
        }
    }

    static byte[]? Bits32(IntPtr hbm, int w, int h)
    {
        var bi = new BITMAPINFO32 { h = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 } };
        var bits = new byte[w * h * 4];
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try { return Native.GetDIBits(dc, hbm, 0, (uint)h, bits, ref bi, 0) == 0 ? null : bits; }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>Draws the source icon with a version band across the bottom.</summary>
    public static Bitmap Badge(Bitmap src, int size, string text, Color bg)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, size, size));

        float bandH = MathF.Round(MathF.Max(7f, size * 0.46f));
        var fmt = StringFormat.GenericTypographic;
        float fontPx = bandH * 0.95f;
        var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF ts = g.MeasureString(text, font, PointF.Empty, fmt);
        while (ts.Width > size - 1 && fontPx > 5f)
        {
            font.Dispose();
            fontPx -= 0.5f;
            font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            ts = g.MeasureString(text, font, PointF.Empty, fmt);
        }
        try
        {
            var band = new RectangleF(0, size - bandH, size, bandH);
            using (var path = Rounded(band, MathF.Max(1.5f, bandH * 0.28f)))
            using (var b = new SolidBrush(bg))
                g.FillPath(b, path);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var tb = new SolidBrush(Color.White);
            g.DrawString(text, font, tb, band.X + (band.Width - ts.Width) / 2f, band.Y + (band.Height - ts.Height) / 2f, fmt);
        }
        finally { font.Dispose(); }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        float d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>ICO with 32-bit DIB frames below 256 px and a PNG frame at 256 px. This layout renders on the taskbar.</summary>
    public static byte[] BuildIco(IReadOnlyList<Bitmap> frames)
    {
        var images = frames.Select(f => f.Width >= 256 ? Png(f) : Dib(f)).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            w.Write((byte)(f.Width >= 256 ? 0 : f.Width));
            w.Write((byte)(f.Height >= 256 ? 0 : f.Height));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write((uint)images[i].Length); w.Write((uint)offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
        w.Flush();
        return ms.ToArray();
    }

    static byte[] Png(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static byte[] Dib(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var px = new byte[w * h * 4];
        var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w * 4, w * 4);
        b.UnlockBits(data);

        int maskStride = (w + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0); bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--) bw.Write(px, y * w * 4, w * 4);
        var mask = new byte[maskStride];
        for (int y = h - 1; y >= 0; y--)
        {
            Array.Clear(mask);
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] == 0) mask[x >> 3] |= (byte)(0x80 >> (x & 7));
            bw.Write(mask);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>The tray icon: the app icon (assets/tavo.ico, embedded) at the small-icon size.</summary>
    public static Icon TrayIcon()
    {
        using var s = typeof(Icons).Assembly.GetManifestResourceStream("TAVO.tavo.ico")
                      ?? throw new InvalidOperationException("embedded resource TAVO.tavo.ico is missing");
        return new Icon(s, SystemInformation.SmallIconSize);
    }
}
