using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
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
        private sealed class ProcCacheItem
        {
            public int ParentPid;
            public string Name = "";
            public string CommandLine = "";
        }

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<int, ProcCacheItem> ProcCache = new Dictionary<int, ProcCacheItem>();
        private static readonly int SelfPid = Process.GetCurrentProcess().Id;
        private static readonly int SelfSessionId = Process.GetCurrentProcess().SessionId;

        // 统一匹配当前用户会话中的 ChatGPT 桌面版与 codex 相关进程：
        // ChatGPT.exe / chatgpt-desktop.exe / Codex.exe / codex.exe /
        // codex-command-runner.exe / codex-core-command-runner.exe /
        // codex-chrome-native-host.exe 等；
        // 排除本工具自身（ChatGptProxyTray.exe）以及 Session 0 常驻的
        // Windows 系统沙箱服务（codex-windows-sandbox-service.exe 等，普通用户无权读命令行/关闭）。
        public static bool IsTargetProcessName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = NormalizeName(name);
            if (lower.Length == 0) return false;
            if (lower.StartsWith("chatgptproxytray", StringComparison.Ordinal)) return false;
            if (lower == "codex-windows-sandbox-service" || lower == "codex-windows-sandbox-setup") return false;

            if (lower == "chatgpt" || lower.StartsWith("chatgpt-", StringComparison.Ordinal) || lower.StartsWith("chatgpt_", StringComparison.Ordinal))
            {
                return true;
            }
            return IsCodexProcessName(lower);
        }

        public static bool IsCodexProcessName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = NormalizeName(name);
            if (lower == "codex-windows-sandbox-service" || lower == "codex-windows-sandbox-setup") return false;
            return lower == "codex" ||
                   lower.StartsWith("codex-", StringComparison.Ordinal) ||
                   lower.StartsWith("codex_", StringComparison.Ordinal);
        }

        // 由浏览器扩展按需拉起的本地桥接进程，不代表 ChatGPT 应用本身正在运行
        private static bool IsBrowserBridgeName(string name)
        {
            return NormalizeName(name) == "codex-chrome-native-host";
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string trimmed = name.Trim();
            int slash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            if (slash >= 0 && slash < trimmed.Length - 1) trimmed = trimmed.Substring(slash + 1);
            string lower = trimmed.ToLowerInvariant();
            if (lower.EndsWith(".exe", StringComparison.Ordinal))
            {
                lower = lower.Substring(0, lower.Length - 4);
            }
            return lower;
        }

        public static List<ProcEntry> Query()
        {
            Dictionary<int, string> live = new Dictionary<int, string>();
            Process[] all = null;
            try
            {
                all = Process.GetProcesses();
                for (int i = 0; i < all.Length; i++)
                {
                    Process process = all[i];
                    try
                    {
                        int pid = process.Id;
                        if (pid == SelfPid) continue;
                        // 仅管理当前用户桌面会话中的进程，自动跳过 Session 0 系统服务和其他用户会话
                        if (process.SessionId != SelfSessionId) continue;
                        string procName = process.ProcessName;
                        if (!IsTargetProcessName(procName)) continue;
                        live[pid] = procName + ".exe";
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("枚举进程失败: " + ex.Message);
                return QueryWmiFallback();
            }

            if (live.Count == 0)
            {
                lock (CacheSync)
                {
                    ProcCache.Clear();
                }
                return new List<ProcEntry>();
            }

            List<int> missingPids = new List<int>();
            lock (CacheSync)
            {
                // 清理已退出或 PID 被复用的旧缓存项
                List<int> stale = new List<int>();
                foreach (KeyValuePair<int, ProcCacheItem> pair in ProcCache)
                {
                    string liveName;
                    if (!live.TryGetValue(pair.Key, out liveName) ||
                        !string.Equals(pair.Value.Name, liveName, StringComparison.OrdinalIgnoreCase))
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Count; i++) ProcCache.Remove(stale[i]);

                foreach (KeyValuePair<int, string> pair in live)
                {
                    if (!ProcCache.ContainsKey(pair.Key)) missingPids.Add(pair.Key);
                }
            }

            if (missingPids.Count > 0)
            {
                FetchMissingFromWmi(missingPids);
            }

            List<ProcEntry> result = new List<ProcEntry>(live.Count);
            lock (CacheSync)
            {
                foreach (KeyValuePair<int, string> pair in live)
                {
                    ProcEntry entry = new ProcEntry();
                    entry.Pid = pair.Key;
                    ProcCacheItem cached;
                    if (ProcCache.TryGetValue(pair.Key, out cached))
                    {
                        entry.ParentPid = cached.ParentPid;
                        entry.Name = string.IsNullOrEmpty(cached.Name) ? pair.Value : cached.Name;
                        entry.CommandLine = cached.CommandLine;
                    }
                    else
                    {
                        entry.Name = pair.Value;
                        entry.CommandLine = "";
                    }
                    result.Add(entry);
                }
            }
            return result;
        }

        private static void FetchMissingFromWmi(List<int> pids)
        {
            const int batchSize = 32;
            for (int offset = 0; offset < pids.Count; offset += batchSize)
            {
                int count = Math.Min(batchSize, pids.Count - offset);
                StringBuilder query = new StringBuilder("SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE ");
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) query.Append(" OR ");
                    query.Append("ProcessId=").Append(pids[offset + i].ToString(CultureInfo.InvariantCulture));
                }

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query.ToString()))
                    {
                        using (ManagementObjectCollection results = searcher.Get())
                        {
                            foreach (ManagementObject item in results)
                            {
                                using (item)
                                {
                                    int pid = Convert.ToInt32(item["ProcessId"], CultureInfo.InvariantCulture);
                                    object parent = item["ParentProcessId"];
                                    ProcCacheItem cached = new ProcCacheItem();
                                    cached.ParentPid = parent == null ? 0 : Convert.ToInt32(parent, CultureInfo.InvariantCulture);
                                    cached.Name = Convert.ToString(item["Name"]) ?? "";
                                    cached.CommandLine = Convert.ToString(item["CommandLine"]) ?? "";
                                    lock (CacheSync)
                                    {
                                        ProcCache[pid] = cached;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("WMI 按需查询命令行失败: " + ex.Message);
                }
            }
        }

        private static List<ProcEntry> QueryWmiFallback()
        {
            List<ProcEntry> list = new List<ProcEntry>();
            try
            {
                const string queryText =
                    "SELECT ProcessId, ParentProcessId, SessionId, Name, CommandLine FROM Win32_Process " +
                    "WHERE Name LIKE 'ChatGPT%' OR Name LIKE 'codex%'";
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(queryText))
                {
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            using (item)
                            {
                                int pid = Convert.ToInt32(item["ProcessId"], CultureInfo.InvariantCulture);
                                object sessionObj = item["SessionId"];
                                int sessionId = sessionObj == null ? -1 : Convert.ToInt32(sessionObj, CultureInfo.InvariantCulture);
                                string name = Convert.ToString(item["Name"]) ?? "";
                                if (pid == SelfPid || sessionId != SelfSessionId || !IsTargetProcessName(name)) continue;
                                ProcEntry entry = new ProcEntry();
                                entry.Pid = pid;
                                object parent = item["ParentProcessId"];
                                entry.ParentPid = parent == null ? 0 : Convert.ToInt32(parent, CultureInfo.InvariantCulture);
                                entry.Name = name;
                                entry.CommandLine = Convert.ToString(item["CommandLine"]) ?? "";
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

            ProcEntry rootChatGpt = FindRootByName(processes, "ChatGPT.exe");
            if (rootChatGpt != null) return rootChatGpt;

            for (int i = 0; i < processes.Count; i++)
            {
                if (string.Equals(processes[i].Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return processes[i];
            }

            ProcEntry rootCodex = FindRootByName(processes, "codex.exe");
            if (rootCodex != null) return rootCodex;

            for (int i = 0; i < processes.Count; i++)
            {
                if (!IsBrowserBridgeName(processes[i].Name)) return processes[i];
            }
            return null;
        }

        private static ProcEntry FindRootByName(List<ProcEntry> processes, string targetName)
        {
            for (int i = 0; i < processes.Count; i++)
            {
                ProcEntry entry = processes[i];
                if (!string.Equals(entry.Name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
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
            return null;
        }

        public static AppState Evaluate(List<ProcEntry> processes, string expectedProxyUrl, out string detectedProxyUrl, out ProcEntry main)
        {
            detectedProxyUrl = "";
            main = FindMain(processes);
            if (processes == null || processes.Count == 0 || main == null) return AppState.NotRunning;
            detectedProxyUrl = ProxyProfile.ExtractProxyServer(main.CommandLine);
            if (string.IsNullOrEmpty(detectedProxyUrl)) return AppState.Unproxied;
            if (string.Equals(detectedProxyUrl, expectedProxyUrl, StringComparison.OrdinalIgnoreCase)) return AppState.Proxied;
            return AppState.ProxiedOtherPort;
        }

        // 过滤出参与生命周期管理（启动冲突检测 / 关闭 / 重启）的进程，
        // 排除由浏览器扩展拉起的本地桥接进程（codex-chrome-native-host.exe）。
        public static List<ProcEntry> FilterLifecycleProcesses(List<ProcEntry> processes)
        {
            List<ProcEntry> result = new List<ProcEntry>();
            if (processes == null) return result;
            for (int i = 0; i < processes.Count; i++)
            {
                if (!IsBrowserBridgeName(processes[i].Name)) result.Add(processes[i]);
            }
            return result;
        }

        public static bool CloseAll(List<ProcEntry> processes, int timeoutMs)
        {
            List<ProcEntry> targets = FilterLifecycleProcesses(processes);
            if (targets.Count == 0) return true;
            Stopwatch stopwatch = Stopwatch.StartNew();

            // 1) 枚举所有可见窗口并逐个发 WM_CLOSE。ChatGPT 是 Electron，
            //    主窗口之外还有若干辅助窗口，全部收到关闭消息才会干净退出；
            //    以前只对 MainWindowHandle 调用 CloseMainWindow，常常拿不到句柄而空等 10 秒。
            HashSet<int> pids = new HashSet<int>();
            for (int i = 0; i < targets.Count; i++) pids.Add(targets[i].Pid);
            ProcEntry main = FindMain(targets);
            int rootPid = main != null ? main.Pid : targets[0].Pid;
            int windows = CloseAppWindows(pids, true);
            if (windows == 0)
            {
                // 窗口隐藏/最小化到托盘时，尝试向根进程的隐藏顶层窗口或主窗口发送关闭信号
                HashSet<int> rootSet = new HashSet<int>();
                rootSet.Add(rootPid);
                windows = CloseAppWindows(rootSet, false);
                if (windows == 0) TryCloseMainWindow(rootPid);
            }
            Log.Info("已向 " + windows + " 个窗口发送 WM_CLOSE（相关进程 " + targets.Count + " 个，根进程 pid=" + rootPid + "）");

            // 2) 轮询等待。只要"根进程退出"或"所有可见窗口消失"，就说明用户侧已经关闭，
            //    给子进程最多 2 秒收尾时间（每 200ms 轮询，提前退完即返回），超时后强制清理。
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (FilterLifecycleProcesses(Query()).Count == 0)
                {
                    Log.Info("ChatGPT 已关闭，耗时 " + stopwatch.ElapsedMilliseconds + " ms");
                    return true;
                }

                bool rootAlive = ProcessExists(rootPid);
                bool windowAlive = HasVisibleWindows(pids);
                if (!rootAlive || !windowAlive)
                {
                    Log.Info("主窗口/根进程已结束（根进程存活=" + rootAlive + "，可见窗口=" + windowAlive + "），等待收尾");
                    if (WaitForExit(2000))
                    {
                        Log.Info("关闭完成: True，耗时 " + stopwatch.ElapsedMilliseconds + " ms");
                        return true;
                    }
                    List<ProcEntry> rest = FilterLifecycleProcesses(Query());
                    if (rest.Count > 0)
                    {
                        HashSet<int> restPids = new HashSet<int>();
                        for (int i = 0; i < rest.Count; i++) restPids.Add(rest[i].Pid);
                        CloseAppWindows(restPids, true);
                        Log.Info("强制清理残留子进程 " + rest.Count + " 个");
                        KillAll(rest);
                        WaitForExit(500);
                    }
                    bool finished = FilterLifecycleProcesses(Query()).Count == 0;
                    Log.Info("关闭完成: " + finished + "，耗时 " + stopwatch.ElapsedMilliseconds + " ms");
                    return finished;
                }
                Thread.Sleep(200);
            }

            // 3) 兜底：对残留窗口再补发一次 WM_CLOSE，然后强杀
            List<ProcEntry> remaining = FilterLifecycleProcesses(Query());
            if (remaining.Count > 0)
            {
                HashSet<int> remainingPids = new HashSet<int>();
                for (int i = 0; i < remaining.Count; i++) remainingPids.Add(remaining[i].Pid);
                CloseAppWindows(remainingPids, true);
            }
            Log.Warn("优雅关闭超时，强制结束 ChatGPT 进程（剩余 " + remaining.Count + " 个）");
            KillAll(remaining);
            WaitForExit(600);
            bool done = FilterLifecycleProcesses(Query()).Count == 0;
            Log.Info("强制关闭完成: " + done + "，耗时 " + stopwatch.ElapsedMilliseconds + " ms");
            return done;
        }

        private static bool WaitForExit(int maxWaitMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
            while (DateTime.UtcNow < end)
            {
                if (FilterLifecycleProcesses(Query()).Count == 0) return true;
                Thread.Sleep(200);
            }
            return FilterLifecycleProcesses(Query()).Count == 0;
        }

        private static void TryCloseMainWindow(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
            }
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

        // 给属于目标进程的顶层窗口发送 WM_CLOSE。
        public static int CloseAppWindows(HashSet<int> pids, bool visibleOnly)
        {
            int sent = 0;
            try
            {
                NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        if (visibleOnly && !NativeMethods.IsWindowVisible(hWnd)) return true;
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
                        if (process.HasExited) continue;
                        process.Kill();
                    }
                }
                catch (ArgumentException)
                {
                    // 进程已自行退出
                }
                catch (InvalidOperationException)
                {
                    // 进程正在退出
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

            using (Process process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("Process.Start 返回 null，无法启动 ChatGPT");
                int pid = process.Id;
                Log.Info("已启动 ChatGPT: pid=" + pid + " exe=" + executable + " args=" + arguments);
                return pid;
            }
        }
    }
}
