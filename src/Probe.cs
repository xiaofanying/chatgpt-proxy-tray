using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatGptProxyTray
{
    internal sealed class ProxyProbeResult
    {
        public bool PortOpen;
        public int Port;
        public bool TunnelOk;
        public string Detail = "";
        public string ProxyUrl = "";
    }

    internal static class ClashProbe
    {
        public static ProxyProbeResult Detect(AppConfig cfg)
        {
            List<int> candidates = new List<int>();
            candidates.Add(cfg.ProxyPort);
            for (int i = 0; i < cfg.FallbackPorts.Count; i++)
            {
                if (!candidates.Contains(cfg.FallbackPorts[i])) candidates.Add(cfg.FallbackPorts[i]);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                int port = candidates[i];
                if (!IsPortOpen(cfg.ProxyHost, port, 600)) continue;

                ProxyProbeResult result = new ProxyProbeResult();
                result.PortOpen = true;
                result.Port = port;
                result.ProxyUrl = cfg.BuildProxyUrl(port);
                if (cfg.VerifyConnect)
                {
                    string detail;
                    result.TunnelOk = TestConnect(cfg.ProxyHost, port, "chatgpt.com", 443, 3000, out detail);
                    result.Detail = detail;
                }
                else
                {
                    result.TunnelOk = true;
                    result.Detail = "未启用 CONNECT 验证";
                }
                return result;
            }

            ProxyProbeResult none = new ProxyProbeResult();
            none.PortOpen = false;
            none.Port = cfg.ProxyPort;
            none.Detail = "端口 " + cfg.ProxyHost + ":" + cfg.ProxyPort + " 未监听";
            return none;
        }

        public static bool IsPortOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult asyncResult = client.BeginConnect(host, port, null, null);
                    if (!asyncResult.AsyncWaitHandle.WaitOne(timeoutMs, false)) return false;
                    client.EndConnect(asyncResult);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TestConnect(string proxyHost, int proxyPort, string targetHost, int targetPort, int timeoutMs, out string detail)
        {
            detail = "";
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult asyncResult = client.BeginConnect(proxyHost, proxyPort, null, null);
                    if (!asyncResult.AsyncWaitHandle.WaitOne(2000, false))
                    {
                        detail = "连接代理超时";
                        return false;
                    }
                    client.EndConnect(asyncResult);

                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.ReadTimeout = timeoutMs;
                        string request = string.Format("CONNECT {0}:{1} HTTP/1.1\r\nHost: {0}:{1}\r\nProxy-Connection: Keep-Alive\r\n\r\n", targetHost, targetPort);
                        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                        stream.Write(requestBytes, 0, requestBytes.Length);
                        stream.Flush();

                        byte[] buffer = new byte[512];
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            detail = "代理无响应";
                            return false;
                        }
                        string response = Encoding.ASCII.GetString(buffer, 0, read);
                        string firstLine = response;
                        int newLine = response.IndexOf('\n');
                        if (newLine > 0) firstLine = response.Substring(0, newLine).Trim();
                        detail = firstLine;
                        if (firstLine.IndexOf(" 200") >= 0) return true;
                        if (firstLine.IndexOf(" 407") >= 0) detail = "代理要求认证 (407)";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                detail = "CONNECT 验证失败: " + ex.Message;
                return false;
            }
        }
    }

    internal sealed class TcpRow
    {
        public int Pid;
        public IPAddress LocalAddress;
        public int LocalPort;
        public IPAddress RemoteAddress;
        public int RemotePort;
        public int State;

        public bool IsLoopbackRemote()
        {
            if (RemoteAddress == null) return true;
            if (IPAddress.IsLoopback(RemoteAddress)) return true;
            string text = RemoteAddress.ToString();
            if (text.StartsWith("::ffff:127.", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal static class TcpTable
    {
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const int TcpTableOwnerPidAll = 5;
        private const int ErrorInsufficientBuffer = 122;

        public static List<TcpRow> GetConnections()
        {
            List<TcpRow> rows = new List<TcpRow>();
            ReadTable(rows, AfInet);
            ReadTable(rows, AfInet6);
            return rows;
        }

        private static void ReadTable(List<TcpRow> rows, int addressFamily)
        {
            int size = 0;
            uint result = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidAll, 0);
            if (result != ErrorInsufficientBuffer || size <= 0) return;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidAll, 0);
                if (result != 0) return;

                int count = Marshal.ReadInt32(buffer);
                int rowSize = addressFamily == AfInet ? 24 : 56;
                long baseOffset = buffer.ToInt64() + 4;
                for (int i = 0; i < count; i++)
                {
                    IntPtr rowPointer = new IntPtr(baseOffset + (long)i * rowSize);
                    TcpRow row = addressFamily == AfInet ? ReadV4(rowPointer) : ReadV6(rowPointer);
                    if (row != null) rows.Add(row);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static TcpRow ReadV4(IntPtr rowPointer)
        {
            uint state = (uint)Marshal.ReadInt32(rowPointer, 0);
            uint localAddress = (uint)Marshal.ReadInt32(rowPointer, 4);
            uint localPort = (uint)Marshal.ReadInt32(rowPointer, 8);
            uint remoteAddress = (uint)Marshal.ReadInt32(rowPointer, 12);
            uint remotePort = (uint)Marshal.ReadInt32(rowPointer, 16);
            int pid = Marshal.ReadInt32(rowPointer, 20);

            TcpRow row = new TcpRow();
            row.Pid = pid;
            row.State = (int)state;
            row.LocalAddress = new IPAddress(BitConverter.GetBytes(localAddress));
            row.LocalPort = ConvertPort(localPort);
            row.RemoteAddress = new IPAddress(BitConverter.GetBytes(remoteAddress));
            row.RemotePort = ConvertPort(remotePort);
            return row;
        }

        private static TcpRow ReadV6(IntPtr rowPointer)
        {
            byte[] localBytes = new byte[16];
            Marshal.Copy(rowPointer, localBytes, 0, 16);
            uint localPort = (uint)Marshal.ReadInt32(rowPointer, 20);
            byte[] remoteBytes = new byte[16];
            Marshal.Copy(new IntPtr(rowPointer.ToInt64() + 24), remoteBytes, 0, 16);
            uint remotePort = (uint)Marshal.ReadInt32(rowPointer, 44);
            uint state = (uint)Marshal.ReadInt32(rowPointer, 48);
            int pid = Marshal.ReadInt32(rowPointer, 52);

            TcpRow row = new TcpRow();
            row.Pid = pid;
            row.State = (int)state;
            row.LocalAddress = new IPAddress(localBytes);
            row.LocalPort = ConvertPort(localPort);
            row.RemoteAddress = new IPAddress(remoteBytes);
            row.RemotePort = ConvertPort(remotePort);
            return row;
        }

        private static int ConvertPort(uint rawPort)
        {
            int port = (int)(rawPort & 0xFFFF);
            return ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);
        }
    }
}
