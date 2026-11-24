using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ChatGptProxyTray
{
    /// <summary>极简 JSON 解析（mihomo API 返回对象/数组/字符串/数字/布尔）。</summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) return null;
            Parser parser = new Parser(text);
            return parser.ParseValue();
        }

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            return value as List<object>;
        }

        public static object Get(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj != null && obj.TryGetValue(key, out value)) return value;
            return null;
        }

        public static string GetString(Dictionary<string, object> obj, string key)
        {
            object value = Get(obj, key);
            if (value == null) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static bool GetBool(Dictionary<string, object> obj, string key)
        {
            object value = Get(obj, key);
            if (value is bool) return (bool)value;
            return false;
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _index;

            public Parser(string text)
            {
                _text = text;
                _index = 0;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (_index >= _text.Length) throw new FormatException("JSON 提前结束");
                char c = _text[_index];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                _index++;
                SkipWhitespace();
                if (_index < _text.Length && _text[_index] == '}') { _index++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    if (_index >= _text.Length || _text[_index] != ':') throw new FormatException("JSON 对象缺少冒号");
                    _index++;
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (_index >= _text.Length) throw new FormatException("JSON 对象未闭合");
                    if (_text[_index] == ',') { _index++; continue; }
                    if (_text[_index] == '}') { _index++; return result; }
                    throw new FormatException("JSON 对象格式错误");
                }
            }

            private List<object> ParseArray()
            {
                List<object> result = new List<object>();
                _index++;
                SkipWhitespace();
                if (_index < _text.Length && _text[_index] == ']') { _index++; return result; }
                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (_index >= _text.Length) throw new FormatException("JSON 数组未闭合");
                    if (_text[_index] == ',') { _index++; continue; }
                    if (_text[_index] == ']') { _index++; return result; }
                    throw new FormatException("JSON 数组格式错误");
                }
            }

            private string ParseString()
            {
                if (_index >= _text.Length || _text[_index] != '"') throw new FormatException("JSON 字符串缺少引号");
                _index++;
                StringBuilder builder = new StringBuilder();
                while (_index < _text.Length)
                {
                    char c = _text[_index++];
                    if (c == '"') return builder.ToString();
                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }
                    if (_index >= _text.Length) throw new FormatException("JSON 转义未结束");
                    char escape = _text[_index++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_index + 4 > _text.Length) throw new FormatException("JSON unicode 转义不完整");
                            string hex = _text.Substring(_index, 4);
                            _index += 4;
                            builder.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default: throw new FormatException("未知的 JSON 转义: \\" + escape);
                    }
                }
                throw new FormatException("JSON 字符串未闭合");
            }

            private object ParseNumber()
            {
                int start = _index;
                while (_index < _text.Length)
                {
                    char c = _text[_index];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _index++;
                    else break;
                }
                string text = _text.Substring(start, _index - start);
                double value;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw new FormatException("非法 JSON 数字: " + text);
                return value;
            }

            private void Expect(string literal)
            {
                if (_index + literal.Length > _text.Length ||
                    string.Compare(_text, _index, literal, 0, literal.Length, StringComparison.Ordinal) != 0)
                    throw new FormatException("JSON 字面量错误，期望 " + literal);
                _index += literal.Length;
            }

            private void SkipWhitespace()
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index])) _index++;
            }
        }
    }

    internal sealed class ClashHttpResponse
    {
        public int Status;
        public string Body = "";
    }

    internal sealed class ClashGroup
    {
        public string Name = "";
        public string Now = "";
        public List<string> Options = new List<string>();
    }

    internal sealed class ClashState
    {
        public bool ApiAvailable;
        public string PipeName = "";
        public string Version = "";
        public string Mode = "";
        public bool TunEnabled;
        public string GlobalNow = "";
        public string Error = "";
        public List<ClashGroup> Groups = new List<ClashGroup>();
        public DateTime UpdatedAt = DateTime.Now;

        public string PrimaryGroupName
        {
            get { return Groups.Count > 0 ? Groups[0].Name : ""; }
        }

        public string PrimaryGroupNow
        {
            get { return Groups.Count > 0 ? Groups[0].Now : ""; }
        }

        public string ModeText
        {
            get { return ClashApi.ModeToText(Mode); }
        }
    }

    internal sealed class ClashConnection
    {
        public string Process = "";
        public string Network = "";
        public string Host = "";
        public string Destination = "";
        public string Rule = "";
        public string RulePayload = "";
        public List<string> Chains = new List<string>();
        public long Upload;
        public long Download;
        public string Start = "";

        public string ChainText
        {
            get { return Chains.Count == 0 ? "（直连）" : string.Join(" > ", Chains.ToArray()); }
        }

        public string TargetText
        {
            get
            {
                if (!string.IsNullOrEmpty(Host) && Destination.Length > 0) return Host + " (" + Destination + ")";
                if (!string.IsNullOrEmpty(Host)) return Host;
                return Destination;
            }
        }

        public string RuleText
        {
            get
            {
                if (string.IsNullOrEmpty(Rule)) return "";
                return string.IsNullOrEmpty(RulePayload) ? Rule : Rule + "/" + RulePayload;
            }
        }
    }

    internal static class ClashApi
    {
        private const string PipePrefix = @"\\.\pipe\";

        public static string FindPipe(string overrideName)
        {
            if (!string.IsNullOrEmpty(overrideName)) return overrideName;
            try
            {
                string[] entries = Directory.GetFiles(PipePrefix);
                string fallback = null;
                for (int i = 0; i < entries.Length; i++)
                {
                    string name = entries[i];
                    if (name.StartsWith(PipePrefix, StringComparison.Ordinal)) name = name.Substring(PipePrefix.Length);
                    string lower = name.ToLowerInvariant();
                    bool isMihomo = lower.IndexOf("mihomo", StringComparison.Ordinal) >= 0;
                    bool isClash = lower.IndexOf("clash", StringComparison.Ordinal) >= 0;
                    if (!isMihomo && !isClash) continue;
                    if (isMihomo && lower.IndexOf("production", StringComparison.Ordinal) >= 0) return name;
                    if (fallback == null) fallback = name;
                }
                return fallback;
            }
            catch (Exception ex)
            {
                Log.Warn("枚举命名管道失败: " + ex.Message);
                return null;
            }
        }

        public static ClashState GetState(AppConfig cfg)
        {
            ClashState state = new ClashState();
            string pipeName = FindPipe(cfg.ClashPipeName);
            if (pipeName == null)
            {
                state.Error = "未找到 mihomo 命名管道（Clash 未运行或未开启外部控制）";
                return state;
            }
            state.PipeName = pipeName;

            string error;
            ClashHttpResponse versionResponse = Send(pipeName, "GET", "/version", null, out error);
            if (versionResponse == null)
            {
                state.Error = "连接 Clash API 失败: " + error;
                return state;
            }
            if (versionResponse.Status != 200)
            {
                state.Error = "Clash API 返回 HTTP " + versionResponse.Status;
                return state;
            }
            try
            {
                Dictionary<string, object> versionObject = Json.AsObject(Json.Parse(versionResponse.Body));
                state.Version = Json.GetString(versionObject, "version");
            }
            catch (Exception ex)
            {
                state.Error = "解析 /version 失败: " + ex.Message;
                return state;
            }

            ClashHttpResponse configsResponse = Send(pipeName, "GET", "/configs", null, out error);
            if (configsResponse != null && configsResponse.Status == 200)
            {
                try
                {
                    Dictionary<string, object> configs = Json.AsObject(Json.Parse(configsResponse.Body));
                    state.Mode = Json.GetString(configs, "mode");
                    state.TunEnabled = Json.GetBool(Json.AsObject(Json.Get(configs, "tun")), "enable");
                }
                catch (Exception ex)
                {
                    Log.Warn("解析 /configs 失败: " + ex.Message);
                }
            }

            ClashHttpResponse proxiesResponse = Send(pipeName, "GET", "/proxies", null, out error);
            if (proxiesResponse != null && proxiesResponse.Status == 200)
            {
                try
                {
                    Dictionary<string, object> root = Json.AsObject(Json.Parse(proxiesResponse.Body));
                    Dictionary<string, object> proxies = Json.AsObject(Json.Get(root, "proxies"));
                    if (proxies != null)
                    {
                        foreach (KeyValuePair<string, object> pair in proxies)
                        {
                            if (string.Equals(pair.Key, "GLOBAL", StringComparison.Ordinal)) continue;
                            Dictionary<string, object> proxy = Json.AsObject(pair.Value);
                            if (proxy == null) continue;
                            if (!string.Equals(Json.GetString(proxy, "type"), "Selector", StringComparison.OrdinalIgnoreCase)) continue;

                            ClashGroup group = new ClashGroup();
                            group.Name = pair.Key;
                            group.Now = Json.GetString(proxy, "now");
                            List<object> all = Json.AsArray(Json.Get(proxy, "all"));
                            if (all != null)
                            {
                                for (int i = 0; i < all.Count; i++)
                                {
                                    group.Options.Add(Convert.ToString(all[i], CultureInfo.InvariantCulture));
                                }
                            }
                            state.Groups.Add(group);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("解析 /proxies 失败: " + ex.Message);
                }
            }

            state.Groups.Sort(CompareGroups);
            state.ApiAvailable = true;
            state.UpdatedAt = DateTime.Now;

            ClashHttpResponse globalResponse = Send(pipeName, "GET", "/proxies/GLOBAL", null, out error);
            if (globalResponse != null && globalResponse.Status == 200)
            {
                try
                {
                    Dictionary<string, object> globalObject = Json.AsObject(Json.Parse(globalResponse.Body));
                    state.GlobalNow = Json.GetString(globalObject, "now");
                }
                catch (Exception ex)
                {
                    Log.Warn("解析 /proxies/GLOBAL 失败: " + ex.Message);
                }
            }

            return state;
        }

        private static int CompareGroups(ClashGroup left, ClashGroup right)
        {
            int rank = GroupRank(left.Name).CompareTo(GroupRank(right.Name));
            if (rank != 0) return rank;
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static int GroupRank(string name)
        {
            if (name.IndexOf("选择", StringComparison.Ordinal) >= 0) return 0;
            if (name.IndexOf("select", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (string.Equals(name, "AI", StringComparison.OrdinalIgnoreCase)) return 2;
            if (name.IndexOf("节点", StringComparison.Ordinal) >= 0) return 3;
            return 4;
        }

        public static string ModeToText(string mode)
        {
            if (string.Equals(mode, "rule", StringComparison.OrdinalIgnoreCase)) return "规则";
            if (string.Equals(mode, "global", StringComparison.OrdinalIgnoreCase)) return "全局";
            if (string.Equals(mode, "direct", StringComparison.OrdinalIgnoreCase)) return "直连";
            return mode.Length == 0 ? "未知" : mode;
        }

        public static bool SetMode(AppConfig cfg, string mode, out string error)
        {
            error = "";
            string patchError;
            bool sent = PatchConfig(cfg, "{\"mode\":\"" + mode + "\"}", out patchError);
            ClashState state = GetState(cfg);
            if (state.ApiAvailable && string.Equals(state.Mode, mode, StringComparison.OrdinalIgnoreCase)) return true;
            if (sent) return true;
            error = patchError;
            return false;
        }

        public static bool SetTun(AppConfig cfg, bool enable, out string error)
        {
            error = "";
            string patchError;
            bool sent = PatchConfig(cfg, "{\"tun\":{\"enable\":" + (enable ? "true" : "false") + "}}", out patchError);
            // 开启/关闭 TUN 时 mihomo 可能重建监听器导致连接断开、拿不到响应，
            // 这里以“实际状态是否已生效”作为最终判据。
            ClashState state = GetState(cfg);
            if (state.ApiAvailable && state.TunEnabled == enable) return true;
            if (sent) return true;
            error = patchError;
            return false;
        }

        public static bool PatchConfig(AppConfig cfg, string body, out string error)
        {
            string pipeName = FindPipe(cfg.ClashPipeName);
            if (pipeName == null)
            {
                error = "未找到 mihomo 命名管道";
                return false;
            }
            ClashHttpResponse response = Send(pipeName, "PATCH", "/configs", body, out error);
            if (response == null) return false;
            if (response.Status >= 200 && response.Status < 300) return true;
            error = "HTTP " + response.Status + (response.Body.Length > 0 ? ": " + Trim(response.Body, 200) : "");
            return false;
        }

        public static bool SelectNode(AppConfig cfg, string group, string node, out string error)
        {
            error = "";
            string pipeName = FindPipe(cfg.ClashPipeName);
            if (pipeName == null)
            {
                error = "未找到 mihomo 命名管道";
                return false;
            }
            string body = "{\"name\":\"" + EscapeJson(node) + "\"}";
            ClashHttpResponse response = Send(pipeName, "PUT", "/proxies/" + Uri.EscapeDataString(group), body, out error);
            if (response != null && response.Status >= 200 && response.Status < 300) return true;

            string httpError = response == null ? error : "HTTP " + response.Status + (response.Body.Length > 0 ? ": " + Trim(response.Body, 200) : "");
            ClashState state = GetState(cfg);
            if (state.ApiAvailable)
            {
                for (int i = 0; i < state.Groups.Count; i++)
                {
                    if (!string.Equals(state.Groups[i].Name, group, StringComparison.Ordinal)) continue;
                    if (string.Equals(state.Groups[i].Now, node, StringComparison.Ordinal)) return true;
                }
            }
            error = httpError;
            return false;
        }

        // 读取 mihomo /connections：每条连接带 metadata（进程/域名/目标 IP）以及命中的规则与代理链。
        // 只有走 Clash 的连接才会出现在这里，用来确认 ChatGPT 实际命中了哪个规则、走了哪个节点。
        public static List<ClashConnection> GetConnections(AppConfig cfg, out string error)
        {
            error = "";
            List<ClashConnection> list = new List<ClashConnection>();
            string pipeName = FindPipe(cfg.ClashPipeName);
            if (pipeName == null)
            {
                error = "未找到 mihomo 命名管道（Clash 未运行）";
                return list;
            }

            ClashHttpResponse response = Send(pipeName, "GET", "/connections", null, out error);
            if (response == null) return list;
            if (response.Status != 200)
            {
                error = "Clash API 返回 HTTP " + response.Status;
                return list;
            }

            try
            {
                Dictionary<string, object> root = Json.AsObject(Json.Parse(response.Body));
                List<object> connections = Json.AsArray(Json.Get(root, "connections"));
                if (connections == null) return list;

                for (int i = 0; i < connections.Count; i++)
                {
                    Dictionary<string, object> item = Json.AsObject(connections[i]);
                    if (item == null) continue;

                    ClashConnection connection = new ClashConnection();
                    Dictionary<string, object> metadata = Json.AsObject(Json.Get(item, "metadata"));
                    if (metadata != null)
                    {
                        connection.Process = Json.GetString(metadata, "process");
                        connection.Network = Json.GetString(metadata, "network");
                        connection.Host = Json.GetString(metadata, "host");
                        string destinationIp = Json.GetString(metadata, "destinationIP");
                        string destinationPort = Json.GetString(metadata, "destinationPort");
                        if (destinationIp.Length > 0)
                        {
                            connection.Destination = destinationIp + (destinationPort.Length > 0 ? ":" + destinationPort : "");
                        }
                    }
                    connection.Rule = Json.GetString(item, "rule");
                    connection.RulePayload = Json.GetString(item, "rulePayload");
                    connection.Upload = GetLong(item, "upload");
                    connection.Download = GetLong(item, "download");
                    connection.Start = Json.GetString(item, "start");

                    List<object> chains = Json.AsArray(Json.Get(item, "chains"));
                    if (chains != null)
                    {
                        for (int j = 0; j < chains.Count; j++)
                        {
                            string chain = Convert.ToString(chains[j], CultureInfo.InvariantCulture);
                            if (!string.IsNullOrEmpty(chain)) connection.Chains.Add(chain);
                        }
                    }
                    list.Add(connection);
                }
            }
            catch (Exception ex)
            {
                error = "解析 /connections 失败: " + ex.Message;
            }
            return list;
        }

        private static long GetLong(Dictionary<string, object> obj, string key)
        {
            object value = Json.Get(obj, key);
            if (value == null) return 0;
            long parsed;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static string EscapeJson(string text)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(c); break;
                }
            }
            return builder.ToString();
        }

        private static string Trim(string text, int max)
        {
            string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= max ? clean : clean.Substring(0, max) + "…";
        }

        public static ClashHttpResponse Send(string pipeName, string method, string path, string jsonBody, out string error)
        {
            error = "";
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(1200);

                    byte[] bodyBytes = jsonBody == null ? new byte[0] : Encoding.UTF8.GetBytes(jsonBody);
                    StringBuilder head = new StringBuilder();
                    head.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
                    head.Append("Host: localhost\r\nAccept: */*\r\nConnection: close\r\n");
                    if (jsonBody != null)
                    {
                        head.Append("Content-Type: application/json\r\nContent-Length: ").Append(bodyBytes.Length).Append("\r\n");
                    }
                    head.Append("\r\n");

                    byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                    pipe.Write(headBytes, 0, headBytes.Length);
                    if (bodyBytes.Length > 0) pipe.Write(bodyBytes, 0, bodyBytes.Length);
                    pipe.Flush();

                    byte[] raw = ReadResponse(pipe, 500, 5000);
                    return ParseResponse(raw, out error);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static byte[] ReadResponse(NamedPipeClientStream pipe, int idleTimeoutMs, int totalTimeoutMs)
        {
            MemoryStream buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(totalTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                IAsyncResult asyncResult = pipe.BeginRead(chunk, 0, chunk.Length, null, null);
                if (!asyncResult.AsyncWaitHandle.WaitOne(idleTimeoutMs, false)) break;
                int read = pipe.EndRead(asyncResult);
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);
                if (IsComplete(buffer.ToArray())) break;
            }
            return buffer.ToArray();
        }

        private static bool IsComplete(byte[] data)
        {
            int headerEnd = IndexOf(data, 0, new byte[] { 13, 10, 13, 10 });
            if (headerEnd < 0) return false;

            string headers = Encoding.ASCII.GetString(data, 0, headerEnd);
            int status = ParseStatus(headers);
            if (status == 204 || status == 304) return true;

            int bodyOffset = headerEnd + 4;
            int contentLength = ParseHeaderInt(headers, "Content-Length");
            if (contentLength >= 0) return data.Length - bodyOffset >= contentLength;

            if (headers.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool complete;
                Dechunk(data, bodyOffset, out complete, false);
                return complete;
            }
            return false;
        }

        private static ClashHttpResponse ParseResponse(byte[] data, out string error)
        {
            error = "";
            if (data == null || data.Length == 0)
            {
                error = "代理 API 无响应";
                return null;
            }

            int headerEnd = IndexOf(data, 0, new byte[] { 13, 10, 13, 10 });
            if (headerEnd < 0) headerEnd = data.Length;

            string headers = Encoding.ASCII.GetString(data, 0, headerEnd);
            ClashHttpResponse response = new ClashHttpResponse();
            response.Status = ParseStatus(headers);

            int bodyOffset = headerEnd + 4;
            if (bodyOffset > data.Length) bodyOffset = data.Length;
            bool chunked = headers.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
            if (chunked)
            {
                bool complete;
                response.Body = Dechunk(data, bodyOffset, out complete, true);
            }
            else
            {
                int contentLength = ParseHeaderInt(headers, "Content-Length");
                int available = data.Length - bodyOffset;
                int length = contentLength >= 0 && contentLength <= available ? contentLength : available;
                response.Body = Encoding.UTF8.GetString(data, bodyOffset, length);
            }
            return response;
        }

        private static string Dechunk(byte[] data, int offset, out bool complete, bool decode)
        {
            complete = false;
            MemoryStream output = new MemoryStream();
            int position = offset;
            while (true)
            {
                int lineEnd = IndexOf(data, position, new byte[] { 13, 10 });
                if (lineEnd < 0) break;

                string sizeLine = Encoding.ASCII.GetString(data, position, lineEnd - position).Trim();
                int semicolon = sizeLine.IndexOf(';');
                if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon).Trim();

                int size;
                if (!int.TryParse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size)) break;
                position = lineEnd + 2;
                if (size == 0)
                {
                    complete = true;
                    break;
                }
                if (position + size + 2 > data.Length) break;
                if (decode) output.Write(data, position, size);
                position += size + 2;
            }
            return decode ? Encoding.UTF8.GetString(output.ToArray()) : "";
        }

        private static int ParseStatus(string headers)
        {
            if (string.IsNullOrEmpty(headers)) return 0;
            int space = headers.IndexOf(' ');
            if (space < 0) return 0;
            int end = headers.IndexOf(' ', space + 1);
            string code = end < 0 ? headers.Substring(space + 1).Trim() : headers.Substring(space + 1, end - space - 1);
            int status;
            return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out status) ? status : 0;
        }

        private static int ParseHeaderInt(string headers, string name)
        {
            string[] lines = headers.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (!string.Equals(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                int value;
                if (int.TryParse(line.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            }
            return -1;
        }

        private static int IndexOf(byte[] data, int start, byte[] pattern)
        {
            if (data == null || pattern == null || pattern.Length == 0) return -1;
            for (int i = start; i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
