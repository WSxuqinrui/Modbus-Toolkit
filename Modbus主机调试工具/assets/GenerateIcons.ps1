param(
    [ValidateSet('Master','Slave')][string]$Role = 'Master',
    [string]$OutputDirectory = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
# Native geometric artwork only. Requires Windows + .NET Framework; no packages or downloads.
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'The .NET Framework C# compiler was not found.' }
$generatorDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ModbusIconGenerator-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $generatorDirectory -Force | Out-Null
$generatorSource = Join-Path $generatorDirectory 'IconGenerator.cs'
$generatorExe = Join-Path $generatorDirectory 'IconGenerator.exe'
$source = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class IconGenerator
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint length, bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
    private const string MasterPath = "M68 192V106L128 154L188 106V192";
    private const string SlavePath = "M180 106H101C83 106 72 116 72 128C72 141 84 148 101 148H155C173 148 184 158 184 170C184 182 173 192 155 192H76";

    private static Color Hex(string value) { return ColorTranslator.FromHtml(value); }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Render geometry at 4x resolution so the 16px frame retains smooth alpha edges.
    private static Bitmap Render(int size, bool master)
    {
        int renderSize = size * 4;
        using (Bitmap large = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(large))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.ScaleTransform(renderSize / 256f, renderSize / 256f);
                using (GraphicsPath panel = RoundedRectangle(8, 8, 240, 240, 52))
                using (LinearGradientBrush background = new LinearGradientBrush(new PointF(8, 8), new PointF(248, 248), Hex("#1B3A60"), Hex("#0D223D")))
                using (Pen outline = new Pen(Hex("#345477"), 2))
                {
                    graphics.FillPath(background, panel);
                    graphics.DrawPath(outline, panel);
                }

                string first = master ? "#55A8FF" : "#42D3DA";
                string last = master ? "#68E7ED" : "#78EAAF";
                using (LinearGradientBrush accent = new LinearGradientBrush(new PointF(54, 50), new PointF(202, 203), Hex(first), Hex(last)))
                using (Pen link = new Pen(accent, 8))
                using (GraphicsPath letter = new GraphicsPath())
                using (Pen stroke = new Pen(accent, 22))
                {
                    link.StartCap = LineCap.Round; link.EndCap = LineCap.Round;
                    graphics.DrawLine(link, 66, 62, 190, 62);
                    graphics.FillEllipse(accent, 54, 50, 24, 24);
                    graphics.FillEllipse(accent, 178, 50, 24, 24);

                    // Font-independent M/S paths; these are original geometry, not a protocol logo.
                    if (master)
                    {
                        letter.AddLines(new PointF[] { new PointF(68, 192), new PointF(68, 106), new PointF(128, 154), new PointF(188, 106), new PointF(188, 192) });
                    }
                    else
                    {
                        letter.StartFigure();
                        letter.AddLine(180, 106, 101, 106);
                        letter.AddBezier(101, 106, 83, 106, 72, 116, 72, 128);
                        letter.AddBezier(72, 128, 72, 141, 84, 148, 101, 148);
                        letter.AddLine(101, 148, 155, 148);
                        letter.AddBezier(155, 148, 173, 148, 184, 158, 184, 170);
                        letter.AddBezier(184, 170, 184, 182, 173, 192, 155, 192);
                        letter.AddLine(155, 192, 76, 192);
                    }
                    stroke.StartCap = LineCap.Round; stroke.EndCap = LineCap.Round; stroke.LineJoin = LineJoin.Round;
                    graphics.DrawPath(stroke, letter);
                }
            }
            Bitmap small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(small))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(large, new Rectangle(0, 0, size, size), 0, 0, renderSize, renderSize, GraphicsUnit.Pixel);
            }
            return small;
        }
    }

    private static byte[] BitmapFrame(Bitmap bitmap)
    {
        int size = bitmap.Width;
        int maskStride = ((size + 31) / 32) * 4;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            // ICO DIB: doubled height includes the bottom-up 1-bit transparency mask.
            writer.Write(40); writer.Write(size); writer.Write(size * 2);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for (int y = size - 1; y >= 0; y--)
                for (int x = 0; x < size; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                }
            for (int y = size - 1; y >= 0; y--)
            {
                byte[] row = new byte[maskStride];
                for (int x = 0; x < size; x++)
                    if (bitmap.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                writer.Write(row);
            }
            writer.Flush(); return stream.ToArray();
        }
    }

    private static byte[] PngFrame(Bitmap bitmap)
    {
        using (MemoryStream stream = new MemoryStream())
        { bitmap.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    }

    private static void WriteIcon(string path, IDictionary<int, Bitmap> frames)
    {
        List<byte[]> payloads = new List<byte[]>();
        foreach (int size in Sizes) payloads.Add(size == 256 ? PngFrame(frames[size]) : BitmapFrame(frames[size]));
        using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Length);
            int offset = 6 + Sizes.Length * 16;
            for (int index = 0; index < Sizes.Length; index++)
            {
                writer.Write((byte)(Sizes[index] == 256 ? 0 : Sizes[index]));
                writer.Write((byte)(Sizes[index] == 256 ? 0 : Sizes[index]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(payloads[index].Length); writer.Write(offset);
                offset += payloads[index].Length;
            }
            foreach (byte[] payload in payloads) writer.Write(payload);
        }
    }

    private static void WriteSvg(string path, bool master)
    {
        string first = master ? "#55A8FF" : "#42D3DA";
        string last = master ? "#68E7ED" : "#78EAAF";
        string title = master ? "Modbus master tool icon" : "Modbus slave simulator icon";
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\" role=\"img\" aria-labelledby=\"title\">\n"
            + "  <title id=\"title\">" + title + "</title>\n"
            + "  <!-- Original, font-independent artwork; no third-party logo or image. -->\n"
            + "  <defs>\n"
            + "    <linearGradient id=\"background\" x1=\"8\" y1=\"8\" x2=\"248\" y2=\"248\" gradientUnits=\"userSpaceOnUse\"><stop stop-color=\"#1B3A60\"/><stop offset=\"1\" stop-color=\"#0D223D\"/></linearGradient>\n"
            + "    <linearGradient id=\"accent\" x1=\"54\" y1=\"50\" x2=\"202\" y2=\"203\" gradientUnits=\"userSpaceOnUse\"><stop stop-color=\"" + first + "\"/><stop offset=\"1\" stop-color=\"" + last + "\"/></linearGradient>\n"
            + "  </defs>\n"
            + "  <rect x=\"8\" y=\"8\" width=\"240\" height=\"240\" rx=\"52\" fill=\"url(#background)\" stroke=\"#345477\" stroke-width=\"2\"/>\n"
            + "  <path d=\"M66 62H190\" fill=\"none\" stroke=\"url(#accent)\" stroke-width=\"8\" stroke-linecap=\"round\"/>\n"
            + "  <g fill=\"url(#accent)\"><circle cx=\"66\" cy=\"62\" r=\"12\"/><circle cx=\"190\" cy=\"62\" r=\"12\"/></g>\n"
            + "  <path d=\"" + (master ? MasterPath : SlavePath) + "\" fill=\"none\" stroke=\"url(#accent)\" stroke-width=\"22\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n"
            + "</svg>\n";
        File.WriteAllText(path, svg, new UTF8Encoding(false));
    }

    private static void WritePreview(string path, IDictionary<int, Bitmap> frames, bool master)
    {
        using (Bitmap preview = new Bitmap(744, 368, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(preview))
        using (Font title = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font label = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush ink = new SolidBrush(Hex("#233851")))
        using (Brush dark = new SolidBrush(Hex("#172A41")))
        {
            graphics.Clear(Hex("#F0F3F7"));
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.DrawString(master ? "MASTER / M" : "SLAVE / S", title, ink, 24, 20);
            graphics.DrawString("256px", label, ink, 24, 46);
            graphics.DrawImageUnscaled(frames[256], 24, 72);
            graphics.DrawString("16px", label, ink, 320, 20);
            graphics.DrawString("32px", label, ink, 418, 20);
            graphics.DrawString("64px", label, ink, 542, 20);
            graphics.DrawImageUnscaled(frames[16], 320, 52);
            graphics.DrawImageUnscaled(frames[32], 418, 46);
            graphics.DrawImageUnscaled(frames[64], 542, 40);
            graphics.FillRectangle(dark, 304, 112, 392, 56);
            graphics.DrawImageUnscaled(frames[16], 320, 132);
            graphics.DrawImageUnscaled(frames[32], 418, 124);
            graphics.DrawImageUnscaled(frames[32], 558, 124);
            graphics.DrawString("16px enlarged 8x", label, ink, 320, 186);
            graphics.DrawString("32px enlarged 4x", label, ink, 514, 186);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(frames[16], new Rectangle(320, 212, 128, 128), 0, 0, 16, 16, GraphicsUnit.Pixel);
            graphics.DrawImage(frames[32], new Rectangle(514, 212, 128, 128), 0, 0, 32, 32, GraphicsUnit.Pixel);
            preview.Save(path, ImageFormat.Png);
        }
    }

    private static Bitmap DecodeFrame(byte[] bytes, int offset, int length, int size)
    {
        // Framework Icon(Stream, 256, 256) selects 128px for a mixed DIB/PNG ICO.
        // Decode the standard 256px PNG frame directly through the Windows icon API.
        if (size == 256)
        {
            byte[] payload = new byte[length];
            Buffer.BlockCopy(bytes, offset, payload, 0, length);
            IntPtr handle = CreateIconFromResourceEx(payload, (uint)length, true, 0x00030000, size, size, 0);
            if (handle == IntPtr.Zero) throw new InvalidDataException("Win32 could not load the 256px icon frame: " + Marshal.GetLastWin32Error());
            try { using (Icon icon = Icon.FromHandle(handle)) return icon.ToBitmap(); }
            finally { DestroyIcon(handle); }
        }
        using (MemoryStream stream = new MemoryStream(bytes))
        using (Icon icon = new Icon(stream, size, size))
            return icon.ToBitmap();
    }

    private static void Validate(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using (BinaryReader reader = new BinaryReader(new MemoryStream(bytes)))
        {
            if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != Sizes.Length) throw new InvalidDataException("Invalid ICO header.");
            int expectedOffset = 6 + Sizes.Length * 16;
            foreach (int expected in Sizes)
            {
                int width = reader.ReadByte(); if (width == 0) width = 256;
                int height = reader.ReadByte(); if (height == 0) height = 256;
                reader.ReadByte(); reader.ReadByte();
                int planes = reader.ReadUInt16(), depth = reader.ReadUInt16();
                int length = reader.ReadInt32(), offset = reader.ReadInt32();
                if (width != expected || height != expected || planes != 1 || depth != 32 || offset != expectedOffset || offset + length > bytes.Length) throw new InvalidDataException("Invalid ICO frame table.");
                expectedOffset += length;
                using (Bitmap bitmap = DecodeFrame(bytes, offset, length, expected))
                {
                    if (bitmap.Width != expected || bitmap.Height != expected) throw new InvalidDataException("Wrong decoded frame size.");
                    if (bitmap.GetPixel(0, 0).A != 0 || bitmap.GetPixel(expected - 1, expected - 1).A != 0 || bitmap.GetPixel(expected / 2, expected / 2).A != 255) throw new InvalidDataException("Invalid transparency or center alpha.");
                    int partial = 0, transparent = 0;
                    for (int y = 0; y < expected; y++)
                        for (int x = 0; x < expected; x++)
                        { byte alpha = bitmap.GetPixel(x, y).A; if (alpha == 0) transparent++; else if (alpha < 255) partial++; }
                    if (partial == 0 || transparent == 0) throw new InvalidDataException("Missing smooth transparent edges.");
                    Console.WriteLine("PASS " + expected + "x" + expected + " 32bpp; transparent=" + transparent + "; antialias=" + partial + "; payload=" + (expected == 256 ? "PNG" : "DIB"));
                }
            }
            if (expectedOffset != bytes.Length) throw new InvalidDataException("Unexpected trailing ICO data.");
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || (args[0] != "Master" && args[0] != "Slave")) return 2;
        bool master = args[0] == "Master";
        string directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        Dictionary<int, Bitmap> frames = new Dictionary<int, Bitmap>();
        try
        {
            foreach (int size in Sizes)
            {
                Bitmap bitmap = Render(size, master); frames.Add(size, bitmap);
                bitmap.Save(Path.Combine(directory, "app-" + size + ".png"), ImageFormat.Png);
            }
            string iconPath = Path.Combine(directory, "app.ico");
            WriteIcon(iconPath, frames);
            WriteSvg(Path.Combine(directory, "app.svg"), master);
            WritePreview(Path.Combine(directory, "preview.png"), frames, master);
            Validate(iconPath);
            Console.WriteLine("Generated " + args[0] + ": " + iconPath + " (" + new FileInfo(iconPath).Length + " bytes)");
            return 0;
        }
        finally { foreach (Bitmap bitmap in frames.Values) bitmap.Dispose(); }
    }
}
'@
[IO.File]::WriteAllText($generatorSource, $source, (New-Object Text.UTF8Encoding($false)))
& $compilerPath /nologo /target:exe /optimize+ /warnaserror+ /reference:System.Drawing.dll "/out:$generatorExe" $generatorSource
if ($LASTEXITCODE -ne 0) { throw 'Icon generator compilation failed.' }
& $generatorExe $Role ([IO.Path]::GetFullPath($OutputDirectory))
if ($LASTEXITCODE -ne 0) { throw 'Icon generation or validation failed.' }
