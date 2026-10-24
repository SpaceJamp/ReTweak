# Generates ReTweak's icons with GDI+ (no external tools).
#
#   pwsh tools/make-icons.ps1
#
# Writes to src/ReTweak.App/Assets:
#   ReTweak.ico          app icon (16-256 px), "Hot R": a white R on a near-black rounded square whose leg is a
#                        thermometer heating from amber to red (a nod to ROG's red-and-black gamer look, not a copy of its logo)
#   tray.ico             tray icon, normal state (16/20/24/32 px)
#   tray-warning.ico     tray icon with an amber status dot
#   tray-critical.ico    tray icon with a red status dot
#   title-<px>.png       the window's title-bar icon at each display scale's exact size
#   notification.png     the icon on ReTweak's toasts
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
    public static byte[] Render(int size, int status)
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
            using var fill = new LinearGradientBrush(bg, Color.FromArgb(0x24, 0x24, 0x2A), Color.FromArgb(0x0E, 0x0E, 0x12), 90f);
            g.FillPath(fill, path);
            if (size >= 32)
            {
                using var edge = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, s / 128f));
                g.DrawPath(edge, path);
            }
        }

        // The R (unit coordinates): stem and a chamfered bowl. Small sizes get a heavier stroke so it doesn't fade.
        PointF P(float x, float y) => new PointF(x * s, y * s);
        float stroke = Math.Max(0.092f * s, size <= 24 ? 2.4f : 0f);
        using (var r = new GraphicsPath())
        {
            r.AddLines(new[]
            {
                P(0.26f, 0.83f), P(0.26f, 0.20f), P(0.615f, 0.20f), P(0.74f, 0.32f),
                P(0.74f, 0.445f), P(0.615f, 0.555f), P(0.26f, 0.555f),
            });
            using var white = new Pen(Color.FromArgb(0xF2, 0xF2, 0xF4), stroke) { LineJoin = LineJoin.Miter };
            g.DrawPath(white, r);
        }

        // The leg is a thermometer: a stem heating from amber to red, into a red bulb.
        var red = Color.FromArgb(0xE5, 0x17, 0x2F);
        var legTop = P(0.48f, 0.555f); var legEnd = P(0.68f, 0.755f);
        // The gradient ends at the bulb's centre: GDI+ repeats it past its end, which made the round cap flash amber.
        using (var heat = new LinearGradientBrush(legTop, P(0.74f, 0.815f), Color.FromArgb(0xFF, 0xB0, 0x20), red))
        using (var leg = new Pen(heat, Math.Max(0.077f * s, size <= 24 ? 2.2f : 0f)) { StartCap = LineCap.Flat, EndCap = LineCap.Round })
            g.DrawLine(leg, legTop, legEnd);
        float bulb = Math.Max(0.085f * s, size <= 24 ? 2.6f : 0f);
        using (var bulbFill = new SolidBrush(red))
            g.FillEllipse(bulbFill, 0.74f * s - bulb, 0.815f * s - bulb, 2 * bulb, 2 * bulb);

        if (status > 0)
        {
            // Top-right: bottom-right is the R's red bulb, which a red (critical) dot would blend into.
            float d = s * 0.46f;
            var dot = new RectangleF(s - d - s * 0.01f, s * 0.01f, d, d);
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

function Write-Icon($name, [int[]]$sizes, [int]$status) {
    [byte[][]]$pngs = foreach ($sz in $sizes) { , [IconMaker]::Render($sz, $status) }
    $path = Join-Path $assets $name
    [IconMaker]::WriteIco($path, $pngs, $sizes)
    "wrote $path"
}

Write-Icon "ReTweak.ico" @(16, 20, 24, 32, 40, 48, 64, 128, 256) 0
Write-Icon "tray.ico" @(16, 20, 24, 32) 0
Write-Icon "tray-warning.ico" @(16, 20, 24, 32) 1
Write-Icon "tray-critical.ico" @(16, 20, 24, 32) 2

# Title bar: 18 DIP at 100/125/150/175/200/250 % display scale, drawn at the exact pixel size so it isn't resampled
# (the .ico has 24 and 32 px but not the 27 px a 150 % display needs, and scaling one of them blurs it).
foreach ($px in 18, 23, 27, 32, 36, 45) {
    [IO.File]::WriteAllBytes((Join-Path $assets "title-$px.png"), [IconMaker]::Render($px, 0))
}
"wrote title-*.png"

# Notifications: Windows scales this down for the toast header and Action Center.
[IO.File]::WriteAllBytes((Join-Path $assets "notification.png"), [IconMaker]::Render(96, 0))
"wrote notification.png"

$png = Join-Path $root "docs/icon.png"
[IO.File]::WriteAllBytes($png, [IconMaker]::Render(256, 0))
"wrote $png"
