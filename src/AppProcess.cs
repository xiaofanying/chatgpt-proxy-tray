using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;

namespace ChatGptProxyTray
{
    internal sealed class ProcEntry
    {
        public int Pid;
        public int ParentPid;
        public string Name = "";
        public string CommandLine = "";
    }

    internal enum AppState
    {
        NotRunning,
        Proxied,
        ProxiedOtherPort,
        Unproxied
    }

    internal static class AppProcess
    {
        private const string QueryText =
            "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process " +
            "WHERE Name='ChatGPT.exe' OR Name='codex.exe' OR Name='codex-command-runner.exe'";

        public static List<ProcEntry> Query()
        {
            List<ProcEntry> list = new List<ProcEntry>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(QueryText))
                {
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            using (item)
                            {
                                ProcEntry entry = new ProcEntry();
                                entry.Pid = Convert.ToInt32(item["ProcessId"]);
                                object parent = item["ParentProcessId"];
                                entry.ParentPid = parent == null ? 0 : Convert.ToInt32(parent);
                                entry.Name = Convert.ToString(item["Name"]);
                                entry.CommandLine = Convert.ToString(item["CommandLine"]);
                                list.Add(entry);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("WMI 进程查询失败: " + ex.Message);
            }
            return list;
        }

        public static ProcEntry FindMain(List<ProcEntry> processes)
        {
            if (processes == null || processes.Count == 0) return null;

            for (int i = 0; i < processes.Count; i++)
            {
                ProcEntry entry = processes[i];
                if (!string.Equals(entry.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) continue;
                bool parentInSet = false;
                for (int j = 0; j < processes.Count; j++)
                {
                    if (processes[j].Pid == entry.ParentPid)
                    {
                        parentInSet = true;
                        break;
                    }
                }
                if (!parentInSet) return entry;
            }

            for (int i = 0; i < processes.Count; i++)
            {
                if (string.Equals(processes[i].Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return processes[i];
            }
            return processes[0];
        }

        public static AppState Evaluate(List<ProcEntry> processes, string expectedProxyUrl, out string detectedProxyUrl, out ProcEntry main)
        {
            detectedProxyUrl = "";
            main = FindMain(processes);
            if (processes == null || processes.Count == 0) return AppState.NotRunning;
            if (main != null) detectedProxyUrl = ProxyProfile.ExtractProxyServer(main.CommandLine);
            if (string.IsNullOrEmpty(detectedProxyUrl)) return AppState.Unproxied;
            if (string.Equals(detectedProxyUrl, expectedProxyUrl, StringComparison.OrdinalIgnoreCase)) return AppState.Proxied;
            return AppState.ProxiedOtherPort;
        }

        public static bool CloseAll(List<ProcEntry> processes, int timeoutMs)
        {
            if (processes == null || processes.Count == 0) return true;
            Stopwatch stopwatch = Stopwatch.StartNew();

            // 1) 枚举所有可见窗口并逐个发 WM_CLOSE。ChatGPT 是 Electron，
            //    主窗口之外还有若干辅助窗口，全部收到关闭消息才会干净退出；
            //    以前只对 MainWindowHandle 调用 CloseMainWindow，常常拿不到句柄而空等 10 秒。
            HashSet<int> pids = new HashSet<int>();
            for (int i = 0; i < processes.Count; i++) pids.Add(processes[i].Pid);
            ProcEntry main = FindMain(processes);
            int rootPid = main != null ? main.Pid : processes[0].Pid;
            int windows = CloseAppWindows(pids);
            Log.Info("已向 " + windows + " 个窗口发送 WM_CLOSE（相关进程 " + processes.Count + " 个，根进程 pid=" + rootPid + "）");

            // 2) 轮询等待。只要"根进程退出"或"所有可见窗口消失"，就说明用户侧已经关闭，
            //    再给子进程 2 秒收尾时间就强制清理，避免死等满 10 秒。
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (Query().Count == 0)
                {
                    Log.Info("ChatGPT 已关闭，耗时 " + stopwatch.ElapsedMilliseconds + " ms");
                    return true;
                }

                bool rootAlive = ProcessExists(rootPid);
                bool windowAlive = HasVisibleWindows(pids);
                if (!rootAlive || !windowAlive)
                {
                    Log.Info("主窗口/根进程已结束（根进程存活=" + rootAlive + "，可见窗口=" + windowAlive + "），等待收尾");
                    Thread.Sleep(2000);
                    List<ProcEntry> rest = Query();
                    if (rest.Count > 0)
                    {
                        HashSet<int> restPids = new HashSet<int>();
                        for (int i = 0; i < rest.Count; i++) restPids.Add(rest[i].Pid);
                        CloseAppWindows(restPids);
                        Log.Info("强制清理残留子进程 " + rest.Count + " 个");
                        KillAll(rest);
                        Thread.Sleep(400);
                    }
                    bool finished = Query().Count == 0;
                    Log.Info("关闭完成: " + finished + "，耗时 " + stopwatch.ElapsedMilliseconds + " ms");
                    return finished;
                }
                Thread.Sleep(200);
            }

            // 3) 兜底：对残留窗口再补发一次 WM_CLOSE，然后强杀
            List<ProcEntry> remaining = Query();
            if (remaining.Count > 0)
            {
                HashSet<int> remainingPids = new HashSet<int>();
                for (int i = 0; i < remaining.Count; i++) remainingPids.Add(remaining[i].Pid);
                CloseAppWindows(remainingPids);
            }
            Log.Warn("优雅关闭超时，强制结束 ChatGPT 进程（剩余 " + remaining.Count + " 个）");
            KillAll(remaining);
            Thread.Sleep(500);
            bool done = Query().Count == 0;
            Log.Info("强制关闭完成: " + done + "，耗时 " + stopwatch.ElapsedMilliseconds + " ms");
            return done;
        }

        private static bool ProcessExists(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        // 是否还有属于目标进程的可见顶层窗口。
        public static bool HasVisibleWindows(HashSet<int> pids)
        {
            bool found = false;
            try
            {
                NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (found) return false;
                    try
                    {
                        if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                        uint pid;
                        NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                        if (pid != 0 && pids.Contains((int)pid)) found = true;
                    }
                    catch
                    {
                    }
                    return !found;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Warn("枚举窗口失败: " + ex.Message);
            }
            return found;
        }

        // 给属于目标进程的所有顶层可见窗口发送 WM_CLOSE。
        public static int CloseAppWindows(HashSet<int> pids)
        {
            int sent = 0;
            try
            {
                NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                        uint pid;
                        NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                        if (pid == 0 || !pids.Contains((int)pid)) return true;
                        if (NativeMethods.PostMessage(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero)) sent++;
                    }
                    catch
                    {
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Warn("枚举窗口失败: " + ex.Message);
            }
            return sent;
        }

        public static void KillAll(List<ProcEntry> processes)
        {
            for (int i = 0; i < processes.Count; i++)
            {
                try
                {
                    using (Process process = Process.GetProcessById(processes[i].Pid))
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("结束进程失败 pid=" + processes[i].Pid + ": " + ex.Message);
                }
            }
        }
    }

    internal static class Launcher
    {
        public static string ResolveExecutable(AppConfig cfg, out string error)
        {
            error = "";
            if (!string.IsNullOrEmpty(cfg.ExePath))
            {
                if (File.Exists(cfg.ExePath)) return cfg.ExePath;
                error = "config.ini 中 target.exe_path 指向的文件不存在: " + cfg.ExePath;
                return null;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string aliasDirectory = Path.Combine(localAppData, "Microsoft", "WindowsApps");
            string aliasPath = Path.Combine(aliasDirectory, cfg.AliasName);
            if (File.Exists(aliasPath)) return aliasPath;

            try
            {
                if (Directory.Exists(aliasDirectory))
                {
                    string[] directories = Directory.GetDirectories(aliasDirectory);
                    for (int i = 0; i < directories.Length; i++)
                    {
                        string candidate = Path.Combine(directories[i], cfg.AliasName);
                        if (File.Exists(candidate)) return candidate;
                    }

                    string[] files = Directory.GetFiles(aliasDirectory, "*chatgpt*.exe");
                    if (files.Length > 0) return files[0];
                }
            }
            catch (Exception ex)
            {
                Log.Warn("扫描执行别名目录失败: " + ex.Message);
            }

            error = "找不到执行别名 " + cfg.AliasName + "（应在 %LOCALAPPDATA%\\Microsoft\\WindowsApps）。" +
                    "请确认 ChatGPT 桌面版已安装，或在 config.ini 的 target.exe_path 中指定真实 exe 路径。";
            return null;
        }

        public static int Launch(AppConfig cfg, int proxyPort, out string executable, out string arguments)
        {
            string error;
            executable = ResolveExecutable(cfg, out error);
            if (executable == null) throw new InvalidOperationException(error);
            arguments = ProxyProfile.BuildArguments(cfg, proxyPort);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = executable;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            ProxyProfile.ApplyEnvironment(psi, cfg, proxyPort);

            string lower = executable.ToLowerInvariant();
            if (lower.IndexOf("\\microsoft\\windowsapps\\", StringComparison.Ordinal) < 0)
            {
                string parent = Path.GetDirectoryName(executable);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) psi.WorkingDirectory = parent;
            }

            Process process = Process.Start(psi);
            if (process == null) throw new InvalidOperationException("Process.Start 返回 null，无法启动 ChatGPT");
            Log.Info("已启动 ChatGPT: pid=" + process.Id + " exe=" + executable + " args=" + arguments);
            return process.Id;
        }
    }
}
