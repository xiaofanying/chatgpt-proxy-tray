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
    //   csc /nologo /target:winexe /platform:x64 /utf8output /out:tools\capture.exe tools\CaptureMain.cs src\*.cs (除 Program.cs)
    // 运行：
    //   tools\capture.exe docs\screenshots
    internal static class CaptureMain
    {
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

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
            CaptureWindow(panel.Handle, Path.Combine(outDir, "panel.png"));
            Log("panel.png " + panel.Width + "x" + panel.Height);

            panel.Hide();
            Pump(500);

            Form backdrop = new Form();
            backdrop.FormBorderStyle = FormBorderStyle.None;
            backdrop.StartPosition = FormStartPosition.Manual;
            backdrop.Bounds = Screen.PrimaryScreen.WorkingArea;
            backdrop.BackColor = Color.FromArgb(248, 248, 248);
            backdrop.ShowInTaskbar = false;
            backdrop.TopMost = true;
            backdrop.Show();
            Pump(500);

            ContextMenuStrip menu = (ContextMenuStrip)GetField(context, "_menu");
            menu.Show(new Point(backdrop.Bounds.Left + 260, backdrop.Bounds.Top + 90));
            Pump(400);
            SetWindowPos(menu.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            Pump(600);
            Rectangle screenBounds = backdrop.Bounds;
            using (Bitmap screen = new Bitmap(screenBounds.Width, screenBounds.Height))
            {
                using (Graphics graphics = Graphics.FromImage(screen))
                {
                    graphics.CopyFromScreen(screenBounds.Left, screenBounds.Top, 0, 0, screenBounds.Size);
                }
                SaveCropped(screen, backdrop.BackColor, Path.Combine(outDir, "menu.png"), 8);
            }
            Log("menu.png saved");
            menu.Close();
            backdrop.Close();
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

        private static void CaptureWindow(IntPtr handle, string path)
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
                        PrintWindow(handle, hdc, 0);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
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
