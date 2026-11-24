using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ChatGptProxyTray
{
    // 开发用截图工具：把托盘菜单和状态面板渲染成 PNG，供 README 使用。
    // 编译（不要带 src\Program.cs，否则有两个 Main）：
    //   csc /nologo /target:winexe /platform:x64 /utf8output /win32icon:app.ico /win32manifest:src\app.manifest /out:tools\capture.exe tools\CaptureMain.cs src\*.cs (除 Program.cs)
    // 运行：
    //   tools\capture.exe docs\screenshots
    internal static class CaptureMain
    {
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : "docs\\screenshots";
            Directory.CreateDirectory(outDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            TrayContext context = new TrayContext();
            Pump(2500);

            StatusPanel panel = (StatusPanel)GetField(context, "_panel");
            context.OpenPanel();
            Pump(3000);
            panel = (StatusPanel)GetField(context, "_panel");
            panel.Hide();
            Pump(300);
            context.OpenPanel();
            Pump(2500);
            CaptureWindow(panel.Handle, Path.Combine(outDir, "panel.png"), true);
            Log("panel.png " + panel.Width + "x" + panel.Height);

            panel.Hide();
            Pump(300);

            ContextMenuStrip menu = (ContextMenuStrip)GetField(context, "_menu");
            menu.AutoClose = false;
            menu.Show(new Point(260, 90));
            Pump(400);
            CaptureWindow(menu.Handle, Path.Combine(outDir, "menu.png"), false);
            Log("menu.png " + menu.Width + "x" + menu.Height);
            menu.Close();
            Pump(300);

            context.ExitThread();
            Thread.Sleep(300);
        }

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("找不到字段 " + name);
            return field.GetValue(target);
        }

        private static void Pump(int milliseconds)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end)
            {
                Application.DoEvents();
                Thread.Sleep(40);
            }
        }

        private static void CaptureWindow(IntPtr handle, string path, bool trimDwmFrame)
        {
            RECT rect;
            GetWindowRect(handle, out rect);
            int width = Math.Max(1, rect.Right - rect.Left);
            int height = Math.Max(1, rect.Bottom - rect.Top);
            using (Bitmap bitmap = new Bitmap(width, height))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = graphics.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT (2) 可在 Windows 10/11 下正确渲染现代窗口标题栏与内容
                        PrintWindow(handle, hdc, 2);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }

                if (trimDwmFrame)
                {
                    RECT frame;
                    const int DwmwaExtendedFrameBounds = 9;
                    if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out frame, Marshal.SizeOf(typeof(RECT))) == 0)
                    {
                        int cropX = Math.Max(0, frame.Left - rect.Left);
                        int cropY = Math.Max(0, frame.Top - rect.Top);
                        int cropW = Math.Min(width - cropX, Math.Max(1, frame.Right - frame.Left));
                        int cropH = Math.Min(height - cropY, Math.Max(1, frame.Bottom - frame.Top));
                        if (cropX > 0 || cropY > 0 || cropW < width || cropH < height)
                        {
                            Rectangle crop = new Rectangle(cropX, cropY, cropW, cropH);
                            using (Bitmap trimmed = bitmap.Clone(crop, bitmap.PixelFormat))
                            {
                                trimmed.Save(path, ImageFormat.Png);
                                return;
                            }
                        }
                    }
                }

                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static void SaveCropped(Bitmap source, Color background, string path, int padding)
        {
            int minX = source.Width;
            int minY = source.Height;
            int maxX = -1;
            int maxY = -1;
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    Color pixel = source.GetPixel(x, y);
                    if (Math.Abs(pixel.R - background.R) > 14 ||
                        Math.Abs(pixel.G - background.G) > 14 ||
                        Math.Abs(pixel.B - background.B) > 14)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < 0)
            {
                source.Save(path, ImageFormat.Png);
                return;
            }

            int left = Math.Max(0, minX - padding);
            int top = Math.Max(0, minY - padding);
            int right = Math.Min(source.Width - 1, maxX + padding);
            int bottom = Math.Min(source.Height - 1, maxY + padding);
            Rectangle crop = new Rectangle(left, top, right - left + 1, bottom - top + 1);
            using (Bitmap result = source.Clone(crop, source.PixelFormat))
            {
                result.Save(path, ImageFormat.Png);
            }
            Log("menu crop " + crop.Width + "x" + crop.Height);
        }

        private static void CaptureScreen(Rectangle bounds, string path)
        {
            using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine(message);
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "capture.log"), message + Environment.NewLine);
        }
    }
}
