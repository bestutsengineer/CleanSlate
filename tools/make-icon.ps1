<#
.SYNOPSIS
    Draws CleanSlate's icons (a rounded tile with a white X) and writes multi-size .ico files:
    CleanSlate.ico (slate gray, normal) and CleanSlate-on.ico (blue, shown while the gray is on).
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\assets')
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class CleanSlateIcon
{
    static Bitmap Render(int size, bool on)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = size / 256f;
            float inset = Math.Max(0.5f, 12 * k);
            RectangleF tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            using (GraphicsPath path = RoundedRect(tile, 60 * k))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(tile, on ? Color.FromArgb(0x5B, 0x9B, 0xF8) : Color.FromArgb(0x8E, 0x97, 0xA5), on ? Color.FromArgb(0x1F, 0x5F, 0xD6) : Color.FromArgb(0x55, 0x5E, 0x6C), LinearGradientMode.Vertical))
                    g.FillPath(fill, path);
                using (Pen edge = new Pen(Color.FromArgb(90, 0x2A, 0x30, 0x38), Math.Max(1f, 4 * k)))
                    g.DrawPath(edge, path);
            }
            using (Pen x = new Pen(Color.White, Math.Max(1.7f, 30 * k)))
            {
                x.StartCap = LineCap.Round;
                x.EndCap = LineCap.Round;
                float a = 86 * k, b = 170 * k;
                g.DrawLine(x, a, a, b, b);
                g.DrawLine(x, b, a, a, b);
            }
        }
        return bmp;
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static byte[] Png(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    // Classic 32-bit DIB icon image (BGRA, bottom-up) plus a 1-bit AND mask.
    static byte[] Dib(Bitmap bmp)
    {
        int n = bmp.Width;
        int maskStride = ((n + 31) / 32) * 4;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] px = new byte[data.Stride * n];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        int stride = data.Stride;
        bmp.UnlockBits(data);

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(n * n * 4 + maskStride * n); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--) w.Write(px, y * stride, n * 4);
            for (int y = n - 1; y >= 0; y--)
            {
                byte[] row = new byte[maskStride];
                for (int x = 0; x < n; x++)
                    if (px[y * stride + x * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(row);
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    public static void Write(string path, bool on)
    {
        int[] sizes = { 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 256 };
        List<byte[]> images = new List<byte[]>();
        foreach (int s in sizes)
            using (Bitmap bmp = Render(s, on)) images.Add(s >= 256 ? Png(bmp) : Dib(bmp));

        using (FileStream fs = File.Create(path))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }
    }
}
'@

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dir = (Resolve-Path $OutDir).Path
[CleanSlateIcon]::Write((Join-Path $dir 'CleanSlate.ico'), $false)
[CleanSlateIcon]::Write((Join-Path $dir 'CleanSlate-on.ico'), $true)
Write-Host "Icons written to $dir"
