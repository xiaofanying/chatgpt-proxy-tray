using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace ChatGptProxyTray
{
    internal sealed class StatusReport
    {
        public ProxyProbeResult Proxy = new ProxyProbeResult();
        public List<ProcEntry> Processes = new List<ProcEntry>();
        public AppState App = AppState.NotRunning;
        public string DetectedProxyUrl = "";
        public string MainCommandLine = "";
        public int ProxiedConnections;
        public int LoopbackConnections;
        public int ExternalConnections;
        public List<string> DirectRemotes = new List<string>();
        public bool CodexPresent;
        public bool CodexViaProxy;
        public DateTime UpdatedAt = DateTime.Now;

        public bool HasLeak
        {
            get { return DirectRemotes.Count > 0; }
        }

        public string AppStateText
        {
            get
            {
                switch (App)
                {
                    case AppState.Proxied:
                        return "已走代理（" + ProxiedConnections + " 条经代理连接）";
                    case AppState.ProxiedOtherPort:
                        return "运行中，代理端口不一致（" + DetectedProxyUrl + "）";
                    case AppState.Unproxied:
                        return "运行中，直连（未注入代理参数）";
                    default:
                        return "未运行";
                }
            }
        }
    }

    internal static class StatusEvaluator
    {
        private const int TcpStateEstablished = 5;

        public static StatusReport Evaluate(AppConfig cfg, ProxyProbeResult probe)
        {
            StatusReport report = new StatusReport();
            report.Proxy = probe;
            report.Processes = AppProcess.Query();

            string detected;
            ProcEntry main;
            report.App = AppProcess.Evaluate(report.Processes, probe.ProxyUrl, out detected, out main);
            report.DetectedProxyUrl = detected;
            if (main != null) report.MainCommandLine = main.CommandLine == null ? "" : main.CommandLine;

            Dictionary<int, string> names = new Dictionary<int, string>();
            for (int i = 0; i < report.Processes.Count; i++)
            {
                ProcEntry entry = report.Processes[i];
                if (!names.ContainsKey(entry.Pid)) names.Add(entry.Pid, entry.Name);
                if (string.Equals(entry.Name, "codex.exe", StringComparison.OrdinalIgnoreCase)) report.CodexPresent = true;
            }

            if (names.Count > 0)
            {
                List<IPAddress> proxyAddresses = ResolveProxyAddresses(cfg.ProxyHost);
                List<TcpRow> rows = TcpTable.GetConnections();
                for (int i = 0; i < rows.Count; i++)
                {
                    TcpRow row = rows[i];
                    if (row.State != TcpStateEstablished) continue;
                    if (!names.ContainsKey(row.Pid)) continue;

                    if (probe.PortOpen && MatchesProxyEndpoint(row, probe.Port, proxyAddresses))
                    {
                        report.ProxiedConnections++;
                        if (string.Equals(names[row.Pid], "codex.exe", StringComparison.OrdinalIgnoreCase)) report.CodexViaProxy = true;
                    }
                    else if (row.IsLoopbackRemote())
                    {
                        report.LoopbackConnections++;
                    }
                    else
                    {
                        report.ExternalConnections++;
                        string remote = row.RemoteAddress.ToString() + ":" + row.RemotePort;
                        if (!report.DirectRemotes.Contains(remote) && report.DirectRemotes.Count < 8) report.DirectRemotes.Add(remote);
                    }
                }
            }

            report.UpdatedAt = DateTime.Now;
            return report;
        }

        // 代理可能运行在本机（127.0.0.1），也可能运行在局域网另一台机器（如 192.168.1.10）。
        // 这里把“远端等于代理主机:端口”的连接一律算作经代理，避免局域网代理被误报成直连泄漏。
        private static List<IPAddress> ResolveProxyAddresses(string host)
        {
            List<IPAddress> addresses = new List<IPAddress>();
            try
            {
                IPAddress literal;
                if (IPAddress.TryParse(host, out literal))
                {
                    addresses.Add(NormalizeAddress(literal));
                    return addresses;
                }
                IPAddress[] resolved = Dns.GetHostAddresses(host);
                for (int i = 0; i < resolved.Length; i++)
                {
                    IPAddress address = NormalizeAddress(resolved[i]);
                    if (address == null) continue;
                    if (!addresses.Contains(address)) addresses.Add(address);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("解析代理主机失败: " + ex.Message);
            }
            return addresses;
        }

        private static bool MatchesProxyEndpoint(TcpRow row, int proxyPort, List<IPAddress> proxyAddresses)
        {
            if (row.RemotePort != proxyPort) return false;
            IPAddress remote = NormalizeAddress(row.RemoteAddress);
            if (remote == null) return false;
            if (proxyAddresses.Count == 0) return row.IsLoopbackRemote();
            for (int i = 0; i < proxyAddresses.Count; i++)
            {
                if (proxyAddresses[i].Equals(remote)) return true;
            }
            return false;
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            if (address == null) return null;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6) return address.MapToIPv4();
            return address;
        }
    }
}
