using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChatGptProxyTray
{
    internal static class Diag
    {
        private const int TcpStateEstablished = 5;

        public static string DirectoryPath
        {
            get { return Path.Combine(Log.BaseDirectory, "diag"); }
        }

        public static string Collect(AppConfig cfg, out string error)
        {
            error = "";
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");

                StringBuilder builder = new StringBuilder();
                builder.AppendLine("== ChatGPT 代理助手 诊断报告 ==");
                builder.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                builder.AppendLine("程序: " + System.Reflection.Assembly.GetExecutingAssembly().Location);
                builder.AppendLine("系统: " + Environment.OSVersion.VersionString + "  64bit=" + Environment.Is64BitOperatingSystem);
                builder.AppendLine();

                builder.AppendLine("== 配置 ==");
                builder.AppendLine("文件: " + cfg.ConfigPath + (File.Exists(cfg.ConfigPath) ? "" : "（缺失，使用默认值）"));
                builder.AppendLine("代理: " + cfg.BuildProxyUrl(cfg.ProxyPort) + "  verify_connect=" + cfg.VerifyConnect);
                builder.AppendLine("备选端口: " + JoinPorts(cfg.FallbackPorts));
                builder.AppendLine("bypass_list: " + cfg.BypassList);
                builder.AppendLine("no_proxy: " + cfg.NoProxy);
                builder.AppendLine("目标: package=" + cfg.PackageName + "  alias=" + cfg.AliasName);
                builder.AppendLine("  exe_path=" + cfg.ExePath + "  extra_args=" + cfg.ExtraArgs);
                builder.AppendLine("Clash 联动: enabled=" + cfg.ClashEnabled + "  auto_launch=" + cfg.AutoLaunchOnClash + "  pipe_name=" + cfg.ClashPipeName);
                builder.AppendLine("守护模式: enabled=" + cfg.GuardEnabled + "  auto_takeover=" + cfg.GuardAutoTakeover);
                builder.AppendLine("界面: refresh=" + cfg.RefreshSeconds + "s  verify=" + cfg.VerifySeconds + "s");
                builder.AppendLine("开机自启: " + (AutoStart.IsEnabled() ? "开：" + AutoStart.CommandLine() : "关"));
                builder.AppendLine();

                ProxyProbeResult probe = ClashProbe.Detect(cfg);
                StatusReport report = StatusEvaluator.Evaluate(cfg, probe);

                builder.AppendLine("== 代理与状态 ==");
                builder.AppendLine("探测: " + probe.ProxyUrl + "  open=" + probe.PortOpen + "  tunnel=" + probe.TunnelOk);
                if (probe.Detail.Length > 0) builder.AppendLine("  " + probe.Detail);
                builder.AppendLine("ChatGPT: " + report.App + " — " + report.AppStateText);
                if (report.DetectedProxyUrl.Length > 0) builder.AppendLine("  检测到的代理: " + report.DetectedProxyUrl);
                if (report.MainCommandLine.Length > 0) builder.AppendLine("  根进程命令行: " + Truncate(report.MainCommandLine, 600));
                builder.AppendLine("连接: 经代理=" + report.ProxiedConnections + "  本地回环=" + report.LoopbackConnections + "  外部=" + report.ExternalConnections);
                builder.AppendLine("codex: 存在=" + report.CodexPresent + "  走代理=" + report.CodexViaProxy);
                builder.AppendLine("疑似直连: " + (report.HasLeak ? string.Join(", ", report.DirectRemotes.ToArray()) : "无"));
                builder.AppendLine();

                List<TcpRow> rows = TcpTable.GetConnections();
                builder.AppendLine("== 相关进程（" + report.Processes.Count + " 个） ==");
                AppendProcesses(builder, report.Processes, rows);
                builder.AppendLine();

                builder.AppendLine("== 相关进程的 ESTABLISHED 连接（最多 40 条） ==");
                AppendConnections(builder, report.Processes, rows);
                builder.AppendLine();

                if (cfg.ClashEnabled)
                {
                    builder.AppendLine("== Clash 联动 ==");
                    ClashState clash = ClashApi.GetState(cfg);
                    if (clash.ApiAvailable)
                    {
                        builder.AppendLine("API: 可用  管道=" + clash.PipeName);
                        builder.AppendLine("版本=" + clash.Version + "  mode=" + clash.Mode + "  tun=" + clash.TunEnabled + "  GLOBAL=" + clash.GlobalNow);
                        builder.AppendLine("策略组 " + clash.Groups.Count + " 个:");
                        for (int i = 0; i < clash.Groups.Count; i++)
                        {
                            ClashGroup group = clash.Groups[i];
                            builder.AppendLine("  - " + group.Name + " → " + group.Now + "（" + group.Options.Count + " 项）");
                        }
                    }
                    else
                    {
                        builder.AppendLine("API: 不可用 — " + clash.Error);
                    }
                    builder.AppendLine();
                }

                builder.AppendLine("== 日志尾部（最后 80 行） ==");
                AppendLogTail(builder, 80);

                File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
                Log.Info("已生成诊断报告: " + path);
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("生成诊断报告失败", ex);
                return null;
            }
        }

        private static void AppendProcesses(StringBuilder builder, List<ProcEntry> processes, List<TcpRow> rows)
        {
            if (processes.Count == 0)
            {
                builder.AppendLine("（没有 ChatGPT / codex 进程）");
                return;
            }
            for (int i = 0; i < processes.Count; i++)
            {
                ProcEntry entry = processes[i];
                int established = 0;
                int external = 0;
                for (int j = 0; j < rows.Count; j++)
                {
                    if (rows[j].Pid != entry.Pid || rows[j].State != TcpStateEstablished) continue;
                    established++;
                    if (!rows[j].IsLoopbackRemote()) external++;
                }
                builder.AppendLine("- pid=" + entry.Pid + " ppid=" + entry.ParentPid + " " + entry.Name +
                                   "  established=" + established + " 外部=" + external);
                if (entry.CommandLine != null && entry.CommandLine.Length > 0)
                {
                    builder.AppendLine("    cmd: " + Truncate(entry.CommandLine, 600));
                }
            }
        }

        private static void AppendConnections(StringBuilder builder, List<ProcEntry> processes, List<TcpRow> rows)
        {
            List<int> pids = new List<int>();
            for (int i = 0; i < processes.Count; i++)
            {
                int pid = processes[i].Pid;
                if (!pids.Contains(pid)) pids.Add(pid);
            }
            if (pids.Count == 0)
            {
                builder.AppendLine("（没有相关进程）");
                return;
            }

            int count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                TcpRow row = rows[i];
                if (row.State != TcpStateEstablished || !pids.Contains(row.Pid)) continue;
                if (count >= 40)
                {
                    builder.AppendLine("…（已截断）");
                    break;
                }
                builder.AppendLine("  pid=" + row.Pid + " " + row.LocalAddress + ":" + row.LocalPort +
                                   " -> " + row.RemoteAddress + ":" + row.RemotePort);
                count++;
            }
            if (count == 0) builder.AppendLine("（没有 ESTABLISHED 连接）");
        }

        private static void AppendLogTail(StringBuilder builder, int maxLines)
        {
            try
            {
                if (!File.Exists(Log.LogFile))
                {
                    builder.AppendLine("（没有日志文件）");
                    return;
                }
                string[] lines = File.ReadAllLines(Log.LogFile, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - maxLines);
                for (int i = start; i < lines.Length; i++) builder.AppendLine(lines[i]);
            }
            catch (Exception ex)
            {
                builder.AppendLine("（读取日志失败: " + ex.Message + "）");
            }
        }

        private static string JoinPorts(List<int> ports)
        {
            string text = "";
            for (int i = 0; i < ports.Count; i++)
            {
                if (i > 0) text += ",";
                text += ports[i];
            }
            return text;
        }

        private static string Truncate(string text, int maxLength)
        {
            if (text == null) return "";
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "…";
        }
    }
}
