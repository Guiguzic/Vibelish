# 生成 assets\app.ico 和 assets\preview.png。
# 图标：蓝色圆角方块上一个对话气泡，里面一来一回两个箭头（互译，不偏向哪种语言）。
# 每个尺寸单独画：24 像素以下去掉气泡，白色箭头直接画在方块上，16 像素按整像素对齐，托盘里才清楚。
# 用法：.\tools\make-icon.ps1 [-Sheet 预览图.png]
param([string]$Sheet)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = Join-Path (Split-Path -Parent $root) 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class VibelishIcon
{
    static readonly Color Top = Color.FromArgb(14, 165, 233);
    static readonly Color Bottom = Color.FromArgb(37, 99, 235);

    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        var d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Pen RoundPen(Color color, float width)
    {
        var pen = new Pen(color, width);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        return pen;
    }

    /// <summary>一来一回两个箭头。坐标用 grid 格，乘 k 换成像素。</summary>
    static void Arrows(Graphics g, Pen pen, float k, float left, float right, float top, float bottom, float head)
    {
        g.DrawLine(pen, left * k, top * k, right * k, top * k);
        g.DrawLines(pen, new[] { new PointF((right - head) * k, (top - head) * k), new PointF(right * k, top * k), new PointF((right - head) * k, (top + head) * k) });
        g.DrawLine(pen, left * k, bottom * k, right * k, bottom * k);
        g.DrawLines(pen, new[] { new PointF((left + head) * k, (bottom - head) * k), new PointF(left * k, bottom * k), new PointF((left + head) * k, (bottom + head) * k) });
    }

    public static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var small = s <= 24;
            var k = s / 256f;
            var m = small ? 0.5f : 8 * k;
            using (var tile = Round(m, m, s - 2 * m, s - 2 * m, small ? s * 0.22f : 56 * k))
            using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Top, Bottom))
            {
                g.FillPath(brush, tile);
                if (s >= 48)
                {
                    using (var edge = new Pen(Color.FromArgb(70, 255, 255, 255), 3 * k))
                        g.DrawPath(edge, tile);
                }
            }

            if (small)
            {
                // 16 格的网格：线宽 2、两条线在第 5、11 格，16 像素时正好盖住两行整像素。
                var u = s / 16f;
                using (var pen = RoundPen(Color.White, 2 * u))
                    Arrows(g, pen, u, 3.5f, 12.5f, 5, 11, 2.5f);
                return bmp;
            }

            // 气泡：圆角矩形加左下角一个小尾巴。
            using (var bubble = Round(34 * k, 50 * k, 188 * k, 140 * k, 46 * k))
            using (var tail = new GraphicsPath())
            using (var white = new SolidBrush(Color.White))
            {
                tail.AddPolygon(new[] { new PointF(66 * k, 178 * k), new PointF(54 * k, 222 * k), new PointF(114 * k, 178 * k) });
                g.FillPath(white, bubble);
                g.FillPath(white, tail);
            }

            using (var pen = RoundPen(Bottom, (s <= 32 ? 19 : 16) * k))
                Arrows(g, pen, k, 80, 176, 100, 142, 22);
        }

        return bmp;
    }

    static byte[] Png(Bitmap bitmap)
    {
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    /// <summary>32 位 BMP 格式的图标项（256 以下的尺寸用这个，老的 API 也认）。</summary>
    static byte[] Dib(Bitmap bitmap)
    {
        var s = bitmap.Width;
        var data = bitmap.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[s * s * 4];
        try
        {
            for (var y = 0; y < s; y++)
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * s * 4, s * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var maskStride = (s + 31) / 32 * 4;
        using (var stream = new MemoryStream())
        using (var w = new BinaryWriter(stream))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (var y = s - 1; y >= 0; y--)
                w.Write(pixels, y * s * 4, s * 4);
            for (var y = s - 1; y >= 0; y--)
            {
                var row = new byte[maskStride];
                for (var x = 0; x < s; x++)
                {
                    if (pixels[(y * s + x) * 4 + 3] == 0)
                        row[x / 8] |= (byte)(0x80 >> (x % 8));
                }
                w.Write(row);
            }
            return stream.ToArray();
        }
    }

    public static void WriteIco(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (var size in sizes)
        {
            using (var bitmap = Render(size))
                images.Add(size >= 256 ? Png(bitmap) : Dib(bitmap));
        }

        using (var stream = File.Create(path))
        using (var w = new BinaryWriter(stream))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            var offset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var image in images)
                w.Write(image);
        }
    }

    /// <summary>检查用：浅色、深色背景上各尺寸并排，16 和 24 再放大看像素。</summary>
    public static void Sheet(string path)
    {
        var sizes = new[] { 256, 64, 48, 32, 24, 20, 16 };
        using (var sheet = new Bitmap(860, 360))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White);
            for (var row = 0; row < 2; row++)
            {
                var y = row * 180;
                using (var back = new SolidBrush(row == 0 ? Color.FromArgb(243, 244, 246) : Color.FromArgb(32, 32, 32)))
                    g.FillRectangle(back, 0, y, 860, 180);
                var x = 16;
                foreach (var size in sizes)
                {
                    var shown = size == 256 ? 128 : size;
                    using (var icon = Render(size))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(icon, new Rectangle(x, y + 26 + (128 - shown) / 2, shown, shown));
                    }
                    x += shown + 22;
                }

                foreach (var size in new[] { 16, 24 })
                {
                    var zoom = size == 16 ? 8 : 5;
                    using (var icon = Render(size))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(icon, new Rectangle(x, y + 26, size * zoom, size * zoom));
                        g.PixelOffsetMode = PixelOffsetMode.Default;
                    }
                    x += size * zoom + 22;
                }
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }
}
'@

$ico = Join-Path $assets 'app.ico'
[VibelishIcon]::WriteIco($ico, [int[]](16, 20, 24, 32, 40, 48, 64, 256))
$preview = [VibelishIcon]::Render(256)
$preview.Save((Join-Path $assets 'preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
if ($Sheet) { [VibelishIcon]::Sheet($Sheet) }
Write-Host ('icon: ' + (Get-Item $ico).Length + ' bytes')
