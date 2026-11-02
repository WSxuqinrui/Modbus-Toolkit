using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ModbusSimulator;

// 仅在内存中绘制控件；不创建服务器、不枚举或打开串口，也不修改发行程序。
internal static class Lamp24Checks
{
    private static int passed;
    private static int failed;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static Bitmap Render(Control control, bool direct)
    {
        Bitmap image = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppArgb);
        if (direct)
        {
            // 异常由反射调用返回给测试，避免 WinForms 将极小尺寸绘制异常转为交互式弹窗。
            using (Graphics graphics = Graphics.FromImage(image))
                control.GetType().GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(control, new object[] { new PaintEventArgs(graphics, new Rectangle(Point.Empty, control.Size)) });
        }
        else
        {
            control.CreateControl();
            control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size));
        }
        return image;
    }

    private static void VerifyLamp(Size size, bool active, string directory)
    {
        using (StateLamp lamp = new StateLamp())
        {
            lamp.Size = size;
            lamp.Active = active;
            using (Bitmap image = Render(lamp, false))
            {
                string label = size.Width + "x" + size.Height + (active ? " on" : " off");
                Color core = active ? Color.FromArgb(18, 178, 113) : Color.FromArgb(153, 170, 190);
                Color halo = active ? Color.FromArgb(219, 247, 232) : Color.FromArgb(237, 242, 247);
                int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
                int haloMinX = image.Width, haloMaxX = -1;
                // 直接识别真实位图的纯色圆芯及晕圈边界，不调用被测控件的几何算法。
                for (int y = 0; y < image.Height; y++)
                    for (int x = 0; x < image.Width; x++)
                    {
                        Color pixel = image.GetPixel(x, y);
                        if (pixel.ToArgb() == core.ToArgb())
                        {
                            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                        }
                        if (pixel.ToArgb() == halo.ToArgb())
                        { haloMinX = Math.Min(haloMinX, x); haloMaxX = Math.Max(haloMaxX, x); }
                    }
                Check(maxX >= minX && haloMaxX >= haloMinX, label + " has a visible core and halo");
                float centerX = image.Width / 2f, centerY = image.Height / 2f;
                Check(Math.Abs((minX + maxX) / 2f - centerX) <= 0.5f &&
                    Math.Abs((minY + maxY) / 2f - centerY) <= 0.5f,
                    label + " core remains concentric");
                float ratio = (maxX - minX + 1f) / (haloMaxX - haloMinX + 1f);
                Check(ratio >= 0.63f && ratio <= 0.80f, label + " core scales with the halo");

                // 圆芯与晕圈本身关于中心对称；与中心反射像素相比变亮的部分即为高光。
                // 此法同时识别落到晕圈上的高光，避免只在预期圆芯内搜索而漏掉突出部分。
                int highlights = 0;
                double sumX = 0, sumY = 0, furthest = 0;
                float radius = (Math.Max(maxX - minX, maxY - minY) + 2f) / 2f;
                for (int y = 0; y < image.Height; y++)
                    for (int x = 0; x < image.Width; x++)
                    {
                        Color pixel = image.GetPixel(x, y);
                        int oppositeX = image.Width - x, oppositeY = image.Height - y;
                        if (oppositeX >= image.Width || oppositeY >= image.Height) continue;
                        Color opposite = image.GetPixel(oppositeX, oppositeY);
                        // 忽略 GDI+ 抗锯齿边界的低幅取样差异，仅识别肉眼可见的高光主体。
                        if (pixel.R <= opposite.R + 24 || pixel.G <= opposite.G + 7 || pixel.B <= opposite.B + 14) continue;
                        if (pixel.R > core.R + (255 - core.R) * 0.60f) continue;
                        highlights++;
                        sumX += x; sumY += y;
                        double dx = x - centerX, dy = y - centerY;
                        double distance = Math.Sqrt(dx * dx + dy * dy);
                        furthest = Math.Max(furthest, distance);
                    }
                Check(highlights > 0, label + " has a visible highlight");
                Check(sumX / highlights < centerX - 0.5 && sumY / highlights < centerY - 0.5,
                    label + " highlight stays in the upper left");
                // 亚像素圆边界允许不足一像素的抗锯齿取样误差。
                Check(furthest <= radius + 0.75, label + " highlight stays inside the core");
                bool clearHalo = true;
                float haloRadius = (haloMaxX - haloMinX + 2f) / 2f;
                for (int y = 0; y < image.Height; y++)
                    for (int x = 0; x < image.Width; x++)
                    {
                        double dx = x - centerX, dy = y - centerY, distance = Math.Sqrt(dx * dx + dy * dy);
                        // 在远离圆芯及外圆抗锯齿的一圈中，白色高光不能污染晕圈。
                        if (distance <= radius + 1.0 || distance >= haloRadius - 1.5) continue;
                        Color pixel = image.GetPixel(x, y);
                        if (pixel.R > halo.R + 2 && pixel.G > halo.G && pixel.B > halo.B) clearHalo = false;
                    }
                Check(clearHalo, label + " highlight does not spill into the halo");
                if (!String.IsNullOrEmpty(directory))
                    image.Save(Path.Combine(directory, "lamp-" + size.Width + "x" + size.Height + (active ? "-on.png" : "-off.png")), ImageFormat.Png);
            }
        }
    }

    private static void VerifySwitch(Size size)
    {
        using (PillSwitch toggle = new PillSwitch())
        {
            toggle.Size = size;
            using (Bitmap off = Render(toggle, true))
            {
                toggle.Checked = true;
                using (Bitmap on = Render(toggle, true))
                    Check(on.Size == off.Size, size.Width + "x" + size.Height + " switch draws both states without invalid geometry");
            }
        }
    }

    private static void VerifySwitchState()
    {
        using (PillSwitch toggle = new PillSwitch())
        {
            int changes = 0;
            toggle.CheckedChanged += delegate { changes++; };
            MethodInfo click = typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
            click.Invoke(toggle, new object[] { EventArgs.Empty });
            Check(toggle.Checked && changes == 1, "switch click enables and raises one change");
            click.Invoke(toggle, new object[] { EventArgs.Empty });
            Check(!toggle.Checked && changes == 2, "switch second click disables and raises one change");
            toggle.Checked = true;
            using (Bitmap image = Render(toggle, true))
                Check(toggle.Checked && changes == 3, "switch painting preserves its checked state");
        }
    }

    private static void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + ex); }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string directory = args.Length > 0 ? args[0] : null;
        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        Size[] lamps = { new Size(32, 32), new Size(50, 50), new Size(20, 20), new Size(38, 24) };
        foreach (Size size in lamps)
        {
            Size current = size;
            Run(delegate { VerifyLamp(current, true, directory); });
            Run(delegate { VerifyLamp(current, false, directory); });
        }
        Size[] switches = { new Size(90, 30), new Size(96, 42), new Size(1, 1), new Size(2, 3), new Size(4, 6), new Size(8, 8), new Size(12, 40) };
        foreach (Size size in switches)
        {
            Size current = size;
            Run(delegate { VerifySwitch(current); });
        }
        Run(VerifySwitchState);
        Console.WriteLine("TOTAL " + passed + ", FAILED " + failed);
        return failed == 0 ? 0 : 1;
    }
}
