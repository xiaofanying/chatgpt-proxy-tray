using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ChatGptProxyTray
{
    internal static class Program
    {
        private const string InstanceMutexName = @"Local\ChatGptProxyTray.SingleInstance";
        private static Mutex _instanceMutex;

        private static bool IsCommandArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return false;
            return arg.StartsWith("--", StringComparison.Ordinal) ||
                   string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(arg, "/?", StringComparison.Ordinal);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && IsCommandArg(args[0]))
            {
                int exitCode = 1;
                try
                {
                    exitCode = RunCommand(args);
                }
                catch (Exception ex)
                {
                    Log.Error("命令行执行失败", ex);
                    Emit("命令行执行失败: " + ex + Environment.NewLine);
                }
                Environment.Exit(exitCode);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            bool createdNew;
            _instanceMutex = new Mutex(true, InstanceMutexName, out createdNew);
            if (!createdNew)
            {
                // 已有实例在托盘中运行：通知已有实例打开状态面板后退出，避免模态框阻塞
                bool signaled = TrayContext.SignalExistingInstance();
                Log.Info(signaled
                    ? "检测到已有托盘实例，已通知打开状态面板，本次启动退出"
                    : "检测到已有托盘实例，本次启动直接退出");
                return;
            }

            try
            {
                Application.Run(new TrayContext());
            }
            finally
            {
                try { _instanceMutex.ReleaseMutex(); } catch { }
                _instanceMutex.Dispose();
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Log.Error("UI 线程未处理异常", e.Exception);
            try
            {
                MessageBox.Show("程序发生错误: " + e.Exception.Message + Environment.NewLine + "详情见 logs\\app.log",
                    "ChatGPT 代理助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Error("未处理异常: " + e.ExceptionObject);
        }

        private static int RunCommand(string[] args)
        {
            string command = args[0].ToLowerInvariant();
            AppConfig cfg = AppConfig.Load();

            if (command == "--help" || command == "-h" || command == "/?")
            {
                Emit(UsageText());
                return 0;
            }
            if (command == "--selftest") return SelfTest(cfg);
            if (command == "--status") return PrintStatus(cfg);
            if (command == "--launch") return LaunchCommand(cfg, HasFlag(args, "--restart"));
            if (command == "--close") return CloseCommand(cfg);
            if (command == "--clash") return PrintClash(cfg);
            if (command == "--clash-mode") return ClashModeCommand(cfg, args);
            if (command == "--clash-node") return ClashNodeCommand(cfg, args);
            if (command == "--clash-tun") return ClashTunCommand(cfg, args);
            if (command == "--clash-auto") return ClashAutoCommand(cfg, args);
            if (command == "--guard") return GuardCommand(cfg, args);
            if (command == "--guard-auto") return GuardAutoCommand(cfg, args);
            if (command == "--autostart") return AutoStartCommand(cfg, args);
            if (command == "--diag") return DiagCommand(cfg);
            if (command == "--connections") return ConnectionsCommand(cfg);
            if (command == "--panel") return PanelCommand();

            Emit("未知参数: " + args[0] + Environment.NewLine + UsageText());
            return 2;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int PrintStatus(AppConfig cfg)
        {
            ProxyProbeResult probe = ClashProbe.Detect(cfg);
            StatusReport report = StatusEvaluator.Evaluate(cfg, probe);
            Emit(BuildReportText(report, cfg));
            bool ok = probe.PortOpen && (probe.TunnelOk || !cfg.VerifyConnect);
            return ok ? 0 : 1;
        }

        private static int SelfTest(AppConfig cfg)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("== 自检 ==");
            builder.AppendLine("配置: " + cfg.ConfigPath + (File.Exists(cfg.ConfigPath) ? "（存在）" : "（缺失）"));
            builder.AppendLine("守护模式: " + (cfg.GuardEnabled ? "开" : "关") + "  auto_takeover=" + cfg.GuardAutoTakeover);
            builder.AppendLine("开机自启: " + (AutoStart.IsEnabled() ? "开" : "关") + " — " + AutoStart.CommandLine());

            bool ok = true;
            string error;
            string executable = Launcher.ResolveExecutable(cfg, out error);
            if (executable != null)
            {
                builder.AppendLine("目标程序: " + executable);
            }
            else
            {
                builder.AppendLine("目标程序: 未找到 — " + error);
                ok = false;
            }

            ProxyProbeResult probe = ClashProbe.Detect(cfg);
            builder.AppendLine("代理: " + cfg.BuildProxyUrl(probe.Port) + "  open=" + probe.PortOpen + "  tunnel=" + probe.TunnelOk);
            if (probe.Detail.Length > 0) builder.AppendLine("  " + probe.Detail);
            if (!probe.PortOpen) ok = false;

            List<ProcEntry> processes = AppProcess.Query();
            builder.AppendLine("ChatGPT 相关进程: " + processes.Count);

            List<TcpRow> rows = TcpTable.GetConnections();
            builder.AppendLine("TCP 连接表条目: " + rows.Count);
            if (rows.Count == 0) ok = false;

            if (cfg.ClashEnabled)
            {
                ClashState clash = ClashApi.GetState(cfg);
                if (clash.ApiAvailable)
                {
                    builder.AppendLine("Clash API: 可用 " + clash.Version + "  mode=" + clash.Mode + "  tun=" + clash.TunEnabled + "  策略组=" + clash.Groups.Count);
                    if (clash.PrimaryGroupName.Length > 0) builder.AppendLine("  当前节点: " + clash.PrimaryGroupName + " → " + clash.PrimaryGroupNow);
                }
                else
                {
                    builder.AppendLine("Clash API: 不可用（" + clash.Error + "）（不影响代理注入，只是无法切换节点/模式）");
                }
            }

            builder.AppendLine("结果: " + (ok ? "通过" : "失败"));
            Emit(builder.ToString());
            return ok ? 0 : 1;
        }

        private static int LaunchCommand(AppConfig cfg, bool restart)
        {
            StringBuilder builder = new StringBuilder();
            ProxyProbeResult probe = ClashProbe.Detect(cfg);
            builder.AppendLine("代理: " + probe.ProxyUrl + "  open=" + probe.PortOpen + "  tunnel=" + probe.TunnelOk);
            if (probe.Detail.Length > 0) builder.AppendLine("  " + probe.Detail);
            if (!probe.PortOpen)
            {
                builder.AppendLine("结果: 代理不可用，取消启动");
                Emit(builder.ToString());
                return 1;
            }

            List<ProcEntry> processes = AppProcess.FilterLifecycleProcesses(AppProcess.Query());
            if (processes.Count > 0)
            {
                if (!restart)
                {
                    builder.AppendLine("结果: ChatGPT 已在运行（" + processes.Count + " 个相关进程），如需重启请加 --restart");
                    Emit(builder.ToString());
                    return 3;
                }
                builder.AppendLine("关闭现有 ChatGPT（" + processes.Count + " 个相关进程）…");
                AppProcess.CloseAll(processes, 10000);
                Thread.Sleep(800);
            }

            string executable;
            string arguments;
            int pid;
            try
            {
                pid = Launcher.Launch(cfg, probe.Port, out executable, out arguments);
            }
            catch (Exception ex)
            {
                builder.AppendLine("结果: 启动失败 — " + ex.Message);
                Emit(builder.ToString());
                return 4;
            }

            builder.AppendLine("已启动: pid=" + pid);
            builder.AppendLine("exe: " + executable);
            builder.AppendLine("args: " + arguments);
            builder.AppendLine("等待 " + cfg.VerifySeconds + " 秒后检测…（期间不要关闭 ChatGPT）");
            Emit(builder.ToString());

            Thread.Sleep(Math.Max(3, cfg.VerifySeconds) * 1000);
            StatusReport report = StatusEvaluator.Evaluate(cfg, probe);
            builder.AppendLine(BuildReportText(report, cfg));
            Emit(builder.ToString());
            return report.App == AppState.Proxied ? 0 : 1;
        }

        private static int PrintClash(AppConfig cfg)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("== Clash 联动 ==");
            builder.AppendLine("配置: enabled=" + cfg.ClashEnabled + "  auto_launch=" + cfg.AutoLaunchOnClash);
            ClashState state = ClashApi.GetState(cfg);
            if (!state.ApiAvailable)
            {
                builder.AppendLine("API: 不可用 — " + state.Error);
                Emit(builder.ToString());
                return 1;
            }
            builder.AppendLine("API: 可用  通道=" + state.EndpointDisplay);
            builder.AppendLine("版本: " + state.Version);
            builder.AppendLine("模式: " + state.Mode + "（" + state.ModeText + "）");
            builder.AppendLine("TUN: " + (state.TunEnabled ? "开启" : "关闭"));
            builder.AppendLine("GLOBAL: " + state.GlobalNow);
            builder.AppendLine("策略组: " + state.Groups.Count);
            for (int i = 0; i < state.Groups.Count; i++)
            {
                ClashGroup group = state.Groups[i];
                builder.AppendLine("  - " + group.Name + " → " + state.FormatNodeWithDelay(group.Now) + "（" + group.Options.Count + " 项）");
            }
            Emit(builder.ToString());
            return 0;
        }

        private static int ClashModeCommand(AppConfig cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Emit("用法: --clash-mode rule|global|direct");
                return 2;
            }
            string mode = args[1].ToLowerInvariant();
            if (mode != "rule" && mode != "global" && mode != "direct")
            {
                Emit("未知模式: " + args[1] + "（可选 rule/global/direct）");
                return 2;
            }
            string error;
            if (!ClashApi.SetMode(cfg, mode, out error))
            {
                Emit("切换模式失败: " + error);
                return 1;
            }
            Emit("已切换模式: " + mode + "（" + ClashApi.ModeToText(mode) + "）");
            return 0;
        }

        private static int ClashNodeCommand(AppConfig cfg, string[] args)
        {
            if (args.Length < 3)
            {
                Emit("用法: --clash-node <策略组> <节点>");
                return 2;
            }
            string error;
            if (!ClashApi.SelectNode(cfg, args[1], args[2], out error))
            {
                Emit("切换节点失败: " + error);
                return 1;
            }
            Emit("已切换节点: " + args[1] + " → " + args[2]);
            return 0;
        }

        private static int ClashTunCommand(AppConfig cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Emit("用法: --clash-tun on|off");
                return 2;
            }
            bool ok;
            bool enable = ParseOnOff(args[1], out ok);
            if (!ok)
            {
                Emit("参数错误: " + args[1] + "（可选 on/off）");
                return 2;
            }
            string error;
            if (!ClashApi.SetTun(cfg, enable, out error))
            {
                Emit("设置 TUN 失败: " + error);
                return 1;
            }
            Emit("TUN 已" + (enable ? "开启" : "关闭"));
            return 0;
        }

        private static int ClashAutoCommand(AppConfig cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Emit("用法: --clash-auto on|off");
                return 2;
            }
            bool ok;
            bool enable = ParseOnOff(args[1], out ok);
            if (!ok)
            {
                Emit("参数错误: " + args[1] + "（可选 on/off）");
                return 2;
            }
            cfg.AutoLaunchOnClash = enable;
            if (!cfg.SetValue("clash", "auto_launch", enable ? "true" : "false"))
            {
                Emit("写入配置文件失败: " + cfg.ConfigPath);
                return 1;
            }
            Emit("自动启动已" + (enable ? "开启" : "关闭") + "（config.ini [clash] auto_launch）");
            return 0;
        }

        private static int CloseCommand(AppConfig cfg)
        {
            List<ProcEntry> processes = AppProcess.FilterLifecycleProcesses(AppProcess.Query());
            if (processes.Count == 0)
            {
                Emit("ChatGPT 未运行");
                return 0;
            }
            bool ok = AppProcess.CloseAll(processes, 10000);
            Emit("已关闭 ChatGPT（剩余相关进程 " + AppProcess.FilterLifecycleProcesses(AppProcess.Query()).Count + " 个）");
            return ok ? 0 : 1;
        }

        private static bool ParseOnOff(string value, out bool ok)
        {
            ok = true;
            string v = value == null ? "" : value.Trim().ToLowerInvariant();
            if (v == "on" || v == "1" || v == "true" || v == "yes") return true;
            if (v == "off" || v == "0" || v == "false" || v == "no") return false;
            ok = false;
            return false;
        }

        private static int GuardCommand(AppConfig cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Emit("用法: --guard on|off");
                return 2;
            }
            bool ok;
            bool enable = ParseOnOff(args[1], out ok);
            if (!ok)
            {
                Emit("参数错误: " + args[1] + "（可选 on/off）");
                return 2;
            }
            cfg.GuardEnabled = enable;
            if (!cfg.SetValue("guard", "enabled", enable ? "true" : "false"))
            {
                Emit("写入配置文件失败: " + cfg.ConfigPath);
                return 1;
            }
            Emit("守护模式已" + (enable ? "开启" : "关闭") + "（config.ini [guard] enabled）" + Environment.NewLine);
            return 0;
        }

        private static int GuardAutoCommand(AppConfig cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Emit("用法: --guard-auto on|off");
                return 2;
            }
            bool ok;
            bool enable = ParseOnOff(args[1], out ok);
            if (!ok)
            {
                Emit("参数错误: " + args[1] + "（可选 on/off）");
                return 2;
            }
            cfg.GuardAutoTakeover = enable;
            if (!cfg.SetValue("guard", "auto_takeover", enable ? "true" : "false"))
            {
                Emit("写入配置文件失败: " + cfg.ConfigPath);
                return 1;
            }
            if (enable && !cfg.GuardEnabled)
            {
                cfg.GuardEnabled = true;
                cfg.SetValue("guard", "enabled", "true");
            }
            Emit("自动接管已" + (enable ? "开启" : "关闭") + "（config.ini [guard] auto_takeover）" +
                 (enable ? "，守护模式同时开启" : "") + Environment.NewLine);
            return 0;
        }

        private static int AutoStartCommand(AppConfig cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Emit("用法: --autostart on|off");
                return 2;
            }
            bool ok;
            bool enable = ParseOnOff(args[1], out ok);
            if (!ok)
            {
                Emit("参数错误: " + args[1] + "（可选 on/off）");
                return 2;
            }
            string error;
            if (!AutoStart.SetEnabled(enable, out error))
            {
                Emit("设置开机自启失败: " + error);
                return 1;
            }
            Emit("开机自启已" + (enable ? "开启: " + AutoStart.CommandLine() : "关闭") + Environment.NewLine);
            return 0;
        }

        private static int DiagCommand(AppConfig cfg)
        {
            string error;
            string path = Diag.Collect(cfg, out error);
            if (path == null)
            {
                Emit("生成诊断报告失败: " + error);
                return 1;
            }
            Emit("诊断报告已生成: " + path + Environment.NewLine);
            return 0;
        }

        private static int ConnectionsCommand(AppConfig cfg)
        {
            ProxyProbeResult probe = ClashProbe.Detect(cfg);
            StatusReport report = StatusEvaluator.Evaluate(cfg, probe);
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("== 连接与命中 ==");
            builder.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("代理: " + (report.Proxy.ProxyUrl.Length > 0 ? report.Proxy.ProxyUrl : cfg.BuildProxyUrl(cfg.ProxyPort)) + "  port_open=" + report.Proxy.PortOpen);
            builder.AppendLine("ChatGPT: " + report.AppStateText);
            if (report.MainCommandLine.Length > 0) builder.AppendLine("根进程命令行: " + Shorten(report.MainCommandLine, 220));

            // 1) 进程级 TCP 连接：确认 ChatGPT 自己的 socket 到底连到了哪里
            List<TcpRow> rows = report.AppConnections;
            Dictionary<int, string> names = new Dictionary<int, string>();
            for (int i = 0; i < report.Processes.Count; i++) names[report.Processes[i].Pid] = report.Processes[i].Name;
            builder.AppendLine("进程 ESTABLISHED 连接:");
            int shown = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                TcpRow row = rows[i];
                if (row.State != 5) continue;
                string name;
                if (!names.TryGetValue(row.Pid, out name)) continue;
                if (shown >= 40) break;
                string kind;
                if (report.Proxy.PortOpen && StatusEvaluator.MatchesProxyEndpoint(row, report.Proxy.Port, report.ProxyAddresses)) kind = "经代理";
                else if (row.IsLoopbackRemote()) kind = "本地";
                else kind = "直连!";
                builder.AppendLine("  " + name + " pid=" + row.Pid + " → " + row.RemoteAddress + ":" + row.RemotePort + " [" + kind + "]");
                shown++;
            }
            if (shown == 0) builder.AppendLine("  （无；ChatGPT 未运行）");

            // 2) Clash 连接表：确认命中了哪条规则、走的是哪个节点
            //    若 Clash 未开启 find-process-mode（metadata.process 为空），通过源端口反查进程
            string error;
            List<ClashConnection> connections = ClashApi.GetConnections(cfg, out error);
            Dictionary<int, string> localPortMap = StatusEvaluator.BuildLocalPortProcessMap(report.Processes, TcpTable.GetConnections());
            builder.AppendLine("Clash /connections 命中（仅 ChatGPT / codex）:");
            int hits = 0;
            for (int i = 0; i < connections.Count; i++)
            {
                ClashConnection connection = connections[i];
                string processName = StatusEvaluator.ResolveConnectionProcess(connection, localPortMap);
                if (string.IsNullOrEmpty(processName)) continue;
                builder.AppendLine("  " + processName + "  " + connection.TargetText +
                                   "  规则=" + connection.RuleText +
                                   "  链路=" + connection.ChainText +
                                   "  上行=" + connection.Upload + "B 下行=" + connection.Download + "B");
                hits++;
            }
            if (hits == 0)
            {
                builder.AppendLine("  （无）" + (string.IsNullOrEmpty(error) ? "" : "  错误: " + error));
            }

            Emit(builder.ToString());
            return 0;
        }

        private static string Shorten(string text, int max)
        {
            if (text == null) return "";
            string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= max ? clean : clean.Substring(0, max) + "…";
        }

        private static int PanelCommand()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            bool createdNew;
            using (Mutex mutex = new Mutex(true, InstanceMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (TrayContext.SignalExistingInstance())
                    {
                        Emit("已唤醒正在运行的托盘状态面板。" + Environment.NewLine);
                        return 0;
                    }
                    Emit("已有托盘实例在运行，请在托盘菜单里打开「状态面板」。" + Environment.NewLine);
                    return 1;
                }
                TrayContext context = new TrayContext();
                context.OpenPanel();
                Application.Run(context);
            }
            return 0;
        }

        private static string BuildReportText(StatusReport report, AppConfig cfg)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("== ChatGPT 代理助手 ==");
            builder.AppendLine("时间: " + report.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("代理: " + report.Proxy.ProxyUrl + "  port_open=" + report.Proxy.PortOpen + "  tunnel=" + report.Proxy.TunnelOk);
            if (report.Proxy.Detail.Length > 0) builder.AppendLine("  " + report.Proxy.Detail);
            builder.AppendLine("ChatGPT: " + report.App + "（" + report.Processes.Count + " 个相关进程）");
            if (report.DetectedProxyUrl.Length > 0) builder.AppendLine("  检测到的代理: " + report.DetectedProxyUrl);
            if (report.MainCommandLine.Length > 0) builder.AppendLine("  根进程命令行: " + report.MainCommandLine);
            builder.AppendLine("连接: 经代理=" + report.ProxiedConnections + "  本地回环=" + report.LoopbackConnections + "  外部=" + report.ExternalConnections);
            builder.AppendLine("codex: 存在=" + report.CodexPresent + "  走代理=" + report.CodexViaProxy);
            builder.AppendLine("疑似直连: " + (report.HasLeak ? JoinRemotes(report) : "无"));
            if (cfg != null)
            {
                builder.AppendLine("守护: " + (cfg.GuardEnabled ? "开" : "关") +
                                   "  自动接管: " + (cfg.GuardAutoTakeover ? "开" : "关") +
                                   "  开机自启: " + (AutoStart.IsEnabled() ? "开" : "关"));
            }
            if (cfg != null && cfg.ClashEnabled)
            {
                ClashState clash = ClashApi.GetState(cfg);
                if (clash.ApiAvailable)
                {
                    builder.AppendLine("Clash: " + clash.Version + "  mode=" + clash.Mode + "  tun=" + clash.TunEnabled + "  策略组=" + clash.Groups.Count);
                    if (clash.PrimaryGroupName.Length > 0)
                    {
                        builder.AppendLine("节点: " + clash.PrimaryGroupName + " → " + clash.PrimaryGroupNow);
                    }
                }
                else
                {
                    builder.AppendLine("Clash API: 不可用（" + clash.Error + "）");
                }
            }
            return builder.ToString();
        }

        private static string JoinRemotes(StatusReport report)
        {
            return string.Join(", ", report.DirectRemotes.ToArray());
        }

        private static string UsageText()
        {
            return
                "ChatGPT 代理助手" + Environment.NewLine +
                Environment.NewLine +
                "用法:" + Environment.NewLine +
                "  ChatGptProxyTray.exe                 启动托盘（默认）" + Environment.NewLine +
                "  ChatGptProxyTray.exe --status        打印代理/ChatGPT/连接状态" + Environment.NewLine +
                "  ChatGptProxyTray.exe --selftest      自检（配置/目标程序/代理/进程/TCP 表/Clash）" + Environment.NewLine +
                "  ChatGptProxyTray.exe --launch        启动 ChatGPT 并注入代理（运行中返回 3）" + Environment.NewLine +
                "  ChatGptProxyTray.exe --launch --restart  先关闭再启动" + Environment.NewLine +
                "  ChatGptProxyTray.exe --close         关闭 ChatGPT" + Environment.NewLine +
                "  ChatGptProxyTray.exe --clash         查看 Clash 联动状态（API/模式/TUN/策略组）" + Environment.NewLine +
                "  ChatGptProxyTray.exe --clash-mode rule|global|direct   切换 Clash 代理模式" + Environment.NewLine +
                "  ChatGptProxyTray.exe --clash-node <策略组> <节点>      切换策略组节点" + Environment.NewLine +
                "  ChatGptProxyTray.exe --clash-tun on|off                开关 TUN" + Environment.NewLine +
                "  ChatGptProxyTray.exe --clash-auto on|off               开关「Clash 就绪时自动启动 ChatGPT」" + Environment.NewLine +
                "  ChatGptProxyTray.exe --guard on|off                    开关守护模式（发现直连时提示，点气泡接管）" + Environment.NewLine +
                "  ChatGptProxyTray.exe --guard-auto on|off               发现直连时自动重启为代理（默认关）" + Environment.NewLine +
                "  ChatGptProxyTray.exe --autostart on|off                开机自启（HKCU Run）" + Environment.NewLine +
                "  ChatGptProxyTray.exe --diag          生成诊断报告到 diag\\diag-*.txt" + Environment.NewLine +
                "  ChatGptProxyTray.exe --connections   打印进程连接与 Clash 规则/节点命中" + Environment.NewLine +
                "  ChatGptProxyTray.exe --panel         启动托盘并打开状态面板（含连接命中列表）" + Environment.NewLine +
                "  ChatGptProxyTray.exe --help          显示本帮助" + Environment.NewLine +
                Environment.NewLine +
                "结果同时写入程序目录的 last-result.txt；退出码 0=成功 1=失败 2=参数错误 3=已在运行 4=启动失败。";
        }

        private static string LastResultPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-result.txt");
        }

        private static void Emit(string text)
        {
            try
            {
                File.WriteAllText(LastResultPath(), text, new UTF8Encoding(false));
            }
            catch
            {
            }

            if (Console.IsOutputRedirected)
            {
                try
                {
                    using (Stream stdout = Console.OpenStandardOutput())
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        stdout.Write(bytes, 0, bytes.Length);
                        stdout.Flush();
                    }
                }
                catch
                {
                }
                return;
            }

            bool attached = false;
            try
            {
                attached = NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
                if (attached)
                {
                    try { Console.OutputEncoding = Encoding.UTF8; } catch { }
                    using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8))
                    {
                        writer.AutoFlush = true;
                        writer.Write(text);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (attached)
                {
                    try { NativeMethods.FreeConsole(); } catch { }
                }
            }
        }
    }
}
