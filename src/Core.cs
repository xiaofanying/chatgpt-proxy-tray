using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ChatGptProxyTray
{
    internal static class Log
    {
        private static readonly object Sync = new object();

        public static string BaseDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string LogDirectory
        {
            get { return Path.Combine(BaseDirectory, "logs"); }
        }

        public static string LogFile
        {
            get { return Path.Combine(LogDirectory, "app.log"); }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }
        public static void Error(string message, Exception ex) { Write("ERROR", message + " | " + ex); }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    FileInfo info = new FileInfo(LogFile);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        string old = LogFile + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogFile, old);
                    }
                    string line = string.Format(CultureInfo.InvariantCulture,
                        "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}", DateTime.Now, level, message, Environment.NewLine);
                    File.AppendAllText(LogFile, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class AppConfig
    {
        public string ProxyHost = "127.0.0.1";
        public int ProxyPort = 7897;
        public List<int> FallbackPorts = new List<int>(new int[] { 7897, 7890, 7891, 10808, 10809, 1080 });
        public string BypassList = "localhost;127.0.0.1;[::1];<local>";
        public string NoProxy = "127.0.0.1,localhost,::1";
        public bool VerifyConnect = true;
        public string PackageName = "OpenAI.Codex";
        public string AliasName = "chatgpt-desktop.exe";
        public string ExePath = "";
        public string ExtraArgs = "";
        public bool ClashEnabled = true;
        public bool AutoLaunchOnClash = false;
        public bool GuardEnabled = true;
        public bool GuardAutoTakeover = false;
        public string ClashPipeName = "";
        public int RefreshSeconds = 3;
        public int VerifySeconds = 8;
        public string ConfigPath = "";

        public string BuildProxyUrl(int port)
        {
            return string.Format(CultureInfo.InvariantCulture, "http://{0}:{1}", ProxyHost, port);
        }

        private const string DefaultContent =
            "# ChatGPT 代理助手配置\r\n" +
            "# 端口为 Clash Verge 的混合端口（默认 7897）\r\n" +
            "\r\n" +
            "[proxy]\r\n" +
            "host=127.0.0.1\r\n" +
            "port=7897\r\n" +
            "# 配置端口不通时，依次尝试这些端口（逗号分隔）\r\n" +
            "fallback_ports=7897,7890,7891,10808,10809,1080\r\n" +
            "# 注入给 Chromium 的 --proxy-bypass-list（分号分隔）\r\n" +
            "bypass_list=localhost;127.0.0.1;[::1];<local>\r\n" +
            "# 注入给子进程的 NO_PROXY（逗号分隔），必须包含本地 127.0.0.1，避免本地网关被代理\r\n" +
            "no_proxy=127.0.0.1,localhost,::1\r\n" +
            "# 启动前用 HTTP CONNECT 验证代理是否真的能出网\r\n" +
            "verify_connect=true\r\n" +
            "\r\n" +
            "[target]\r\n" +
            "package_name=OpenAI.Codex\r\n" +
            "alias_name=chatgpt-desktop.exe\r\n" +
            "# 可选：手动指定 ChatGPT.exe 的真实路径（留空则自动使用 MSIX 执行别名）\r\n" +
            "exe_path=\r\n" +
            "# 追加到命令行的额外 Chromium 参数（留空即可）\r\n" +
            "extra_args=\r\n" +
            "\r\n" +
            "[clash]\r\n" +
            "# 是否启用 Clash 联动（读取模式/节点、切换节点、TUN）\r\n" +
            "enabled=true\r\n" +
            "# Clash 端口就绪且 ChatGPT 未运行时自动启动（默认关）\r\n" +
            "auto_launch=false\r\n" +
            "# 手动指定 mihomo API 命名管道名（留空则自动发现）\r\n" +
            "pipe_name=\r\n" +
            "\r\n" +
            "[guard]\r\n" +
            "# 守护模式：托盘发现 ChatGPT 运行却没走代理（例如从开始菜单启动）时弹出提示\r\n" +
            "enabled=true\r\n" +
            "# 发现直连时自动关闭并重启为代理（会中断当前会话，默认关）\r\n" +
            "auto_takeover=false\r\n" +
            "\r\n" +
            "[ui]\r\n" +
            "refresh_seconds=3\r\n" +
            "verify_seconds=8\r\n";

        public static AppConfig Load()
        {
            AppConfig cfg = new AppConfig();
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            cfg.ConfigPath = path;
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, DefaultContent, Encoding.UTF8);
                    Log.Info("已生成默认配置: " + path);
                    return cfg;
                }

                string section = "";
                foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    ApplyValue(cfg, section + "." + key, value);
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败，使用默认配置", ex);
            }
            return cfg;
        }

        private static void ApplyValue(AppConfig cfg, string key, string value)
        {
            switch (key)
            {
                case "proxy.host":
                    if (value.Length > 0) cfg.ProxyHost = value;
                    break;
                case "proxy.port":
                    cfg.ProxyPort = ParseInt(value, cfg.ProxyPort);
                    break;
                case "proxy.fallback_ports":
                    cfg.FallbackPorts = ParsePorts(value);
                    break;
                case "proxy.bypass_list":
                    if (value.Length > 0) cfg.BypassList = value;
                    break;
                case "proxy.no_proxy":
                    if (value.Length > 0) cfg.NoProxy = value;
                    break;
                case "proxy.verify_connect":
                    cfg.VerifyConnect = ParseBool(value, cfg.VerifyConnect);
                    break;
                case "target.package_name":
                    if (value.Length > 0) cfg.PackageName = value;
                    break;
                case "target.alias_name":
                    if (value.Length > 0) cfg.AliasName = value;
                    break;
                case "target.exe_path":
                    cfg.ExePath = value;
                    break;
                case "target.extra_args":
                    cfg.ExtraArgs = value;
                    break;
                case "clash.enabled":
                    cfg.ClashEnabled = ParseBool(value, cfg.ClashEnabled);
                    break;
                case "clash.auto_launch":
                    cfg.AutoLaunchOnClash = ParseBool(value, cfg.AutoLaunchOnClash);
                    break;
                case "clash.pipe_name":
                    cfg.ClashPipeName = value;
                    break;
                case "guard.enabled":
                    cfg.GuardEnabled = ParseBool(value, cfg.GuardEnabled);
                    break;
                case "guard.auto_takeover":
                    cfg.GuardAutoTakeover = ParseBool(value, cfg.GuardAutoTakeover);
                    break;
                case "ui.refresh_seconds":
                    cfg.RefreshSeconds = Math.Max(1, ParseInt(value, cfg.RefreshSeconds));
                    break;
                case "ui.verify_seconds":
                    cfg.VerifySeconds = Math.Max(3, ParseInt(value, cfg.VerifySeconds));
                    break;
            }
        }

        public bool SetValue(string section, string key, string value)
        {
            try
            {
                List<string> lines = new List<string>(File.ReadAllLines(ConfigPath, Encoding.UTF8));
                bool inSection = false;
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inSection = line.Substring(1, line.Length - 2).Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                    lines[i] = key + "=" + value;
                    found = true;
                    break;
                }
                if (!found)
                {
                    lines.Add("");
                    lines.Add("[" + section + "]");
                    lines.Add(key + "=" + value);
                }
                File.WriteAllLines(ConfigPath, lines.ToArray(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("写入配置文件失败", ex);
                return false;
            }
        }

        private static int ParseInt(string value, int fallback)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            string v = value.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return fallback;
        }

        private static List<int> ParsePorts(string value)
        {
            List<int> ports = new List<int>();
            if (!string.IsNullOrEmpty(value))
            {
                string[] parts = value.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    int port = ParseInt(parts[i].Trim(), 0);
                    if (port > 0 && port <= 65535 && !ports.Contains(port)) ports.Add(port);
                }
            }
            return ports;
        }
    }

    internal static class ProxyProfile
    {
        public static void ApplyEnvironment(ProcessStartInfo psi, AppConfig cfg, int proxyPort)
        {
            string url = cfg.BuildProxyUrl(proxyPort);
            psi.EnvironmentVariables["HTTP_PROXY"] = url;
            psi.EnvironmentVariables["HTTPS_PROXY"] = url;
            psi.EnvironmentVariables["ALL_PROXY"] = url;
            psi.EnvironmentVariables["http_proxy"] = url;
            psi.EnvironmentVariables["https_proxy"] = url;
            psi.EnvironmentVariables["all_proxy"] = url;
            psi.EnvironmentVariables["NO_PROXY"] = cfg.NoProxy;
            psi.EnvironmentVariables["no_proxy"] = cfg.NoProxy;
            psi.EnvironmentVariables["NODE_USE_ENV_PROXY"] = "1";
        }

        public static string BuildArguments(AppConfig cfg, int proxyPort)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("--proxy-server=").Append(cfg.BuildProxyUrl(proxyPort));
            builder.Append(" --proxy-bypass-list=").Append(cfg.BypassList);
            builder.Append(" --disable-quic");
            if (!string.IsNullOrEmpty(cfg.ExtraArgs)) builder.Append(' ').Append(cfg.ExtraArgs.Trim());
            return builder.ToString();
        }

        public static string ExtractProxyServer(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return "";
            const string token = "--proxy-server=";
            int index = commandLine.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return "";
            int start = index + token.Length;
            int end = start;
            while (end < commandLine.Length && commandLine[end] != ' ' && commandLine[end] != '\t') end++;
            string value = commandLine.Substring(start, end - start).Trim().Trim('"');
            return value;
        }
    }
}
