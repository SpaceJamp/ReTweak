# Generates ReTweak's icons with GDI+ (no external tools).
#
#   pwsh tools/make-icons.ps1
#
# Writes to src/ReTweak.App/Assets:
#   ReTweak.ico          app icon (16-256 px): lightning bolt on a dark rounded square,
#                        with six connector "pins" under it at 48 px and up
#   tray.ico             tray icon, normal state (16/20/24/32 px)
#   tray-warning.ico     tray icon with an amber status dot
#   tray-critical.ico    tray icon with a red status dot
# and docs/icon.png (256 px) for the README.
#
# Each size is drawn separately (not scaled down) so small sizes stay crisp.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src/ReTweak.App/Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

Add-Type -AssemblyName System.Drawing
# PowerShell 7 splits GDI+ across several assemblies; reference whichever are loaded.
$refs = @("System.Drawing", "System.Drawing.Primitives", "System.Drawing.Common", "System.Private.Windows.GdiPlus",
          "System.Private.Windows.Core", "System.Runtime", "System.IO") |
    Where-Object { try { [void][Reflection.Assembly]::Load($_); $true } catch { $false } }
Add-Type -ReferencedAssemblies $refs -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconMaker
{
    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // status: 0 = none, 1 = warning dot, 2 = critical dot
    public static byte[] Render(int size, bool pins, int status)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        float inset = size <= 20 ? 0.5f : s * 0.03f;
        var bg = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
        using (var path = Rounded(bg, s * 0.22f))
        {
            using var fill = new LinearGradientBrush(bg, Color.FromArgb(0x2A, 0x31, 0x40), Color.FromArgb(0x10, 0x13, 0x1A), 90f);
            g.FillPath(fill, path);
            if (size >= 32)
            {
                using var edge = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, s / 128f));
                g.DrawPath(edge, path);
            }
        }

        // Lightning bolt (unit coordinates), squeezed upward when the pin row is drawn
        float top = pins ? 0.10f : 0.12f, bottom = pins ? 0.72f : 0.88f;
        PointF P(float x, float y) => new PointF(x * s, (top + y * (bottom - top)) * s);
        var bolt = new[]
        {
            P(0.53f, 0.00f), P(0.25f, 0.56f), P(0.46f, 0.56f), P(0.38f, 1.00f),
            P(0.76f, 0.38f), P(0.55f, 0.38f), P(0.69f, 0.00f),
        };
        var boltBounds = new RectangleF(0.25f * s, top * s, 0.51f * s, (bottom - top) * s);
        using (var boltFill = new LinearGradientBrush(boltBounds, Color.FromArgb(0xFF, 0xD1, 0x4A), Color.FromArgb(0xFF, 0x5A, 0x2E), 90f))
            g.FillPolygon(boltFill, bolt);

        if (pins)
        {
            // Six connector pins, like the dashboard's per-pin bars
            float y = 0.79f * s, h = 0.09f * s, left = 0.20f * s, width = 0.60f * s;
            float gap = width * 0.06f, w = (width - gap * 5) / 6;
            using var pinFill = new SolidBrush(Color.FromArgb(0x2E, 0xCC, 0x71));
            for (int i = 0; i < 6; i++)
                using (var pin = Rounded(new RectangleF(left + i * (w + gap), y, w, h), Math.Min(w, h) * 0.3f))
                    g.FillPath(pinFill, pin);
        }

        if (status > 0)
        {
            float d = s * 0.46f;
            var dot = new RectangleF(s - d - s * 0.01f, s - d - s * 0.01f, d, d);
            using var ring = new SolidBrush(Color.FromArgb(0x10, 0x13, 0x1A));
            g.FillEllipse(ring, dot);
            dot.Inflate(-s * 0.06f, -s * 0.06f);
            var c = status == 2 ? Color.FromArgb(0xE2, 0x26, 0x3C) : Color.FromArgb(0xF5, 0xA6, 0x23);
            using var fill = new SolidBrush(c);
            g.FillEllipse(fill, dot);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // ICO with PNG-compressed entries (supported since Windows Vista)
    public static void WriteIco(string path, byte[][] pngs, int[] sizes)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Length);
        int offset = 6 + 16 * pngs.Length;
        for (int i = 0; i < pngs.Length; i++)
        {
            int sz = sizes[i];
            w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)(sz >= 256 ? 0 : sz));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }
}
"@

function Write-Icon($name, [int[]]$sizes, [int]$status, [bool]$allowPins) {
    [byte[][]]$pngs = foreach ($sz in $sizes) { , [IconMaker]::Render($sz, ($allowPins -and $sz -ge 48), $status) }
    $path = Join-Path $assets $name
    [IconMaker]::WriteIco($path, $pngs, $sizes)
    "wrote $path"
}

Write-Icon "ReTweak.ico" @(16, 20, 24, 32, 40, 48, 64, 128, 256) 0 $true
Write-Icon "tray.ico" @(16, 20, 24, 32) 0 $false
Write-Icon "tray-warning.ico" @(16, 20, 24, 32) 1 $false
Write-Icon "tray-critical.ico" @(16, 20, 24, 32) 2 $false

$png = Join-Path $root "docs/icon.png"
[IO.File]::WriteAllBytes($png, [IconMaker]::Render(256, $true, 0))
"wrote $png"
