using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace ChatGptProxyTray
{
    // 状态面板：实时显示代理/ChatGPT/Clash 状态，并列出经过 Clash 的连接与规则命中。
    internal sealed class StatusPanel : Form
    {
        private sealed class BufferedListView : ListView
        {
            public BufferedListView()
            {
                DoubleBuffered = true;
            }
        }

        private readonly TrayContext _context;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Label _labelProxy;
        private readonly Label _labelApp;
        private readonly Label _labelConnections;
        private readonly Label _labelClash;
        private readonly Label _labelGuard;
        private readonly Label _labelStatus;
        private readonly BufferedListView _list;
        private readonly Button _buttonStart;
        private readonly Button _buttonRestart;
        private readonly Button _buttonClose;
        private DateTime _lastConnectionsAt = DateTime.MinValue;
        private bool _loadingConnections;
        private int _lastLoggedCount = -1;
        private string _lastLoggedError;

        public StatusPanel(TrayContext context)
        {
            _context = context;
            Text = "ChatGPT 代理助手 · 状态面板";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(760, 460);
            MaximizeBox = true;
            MinimizeBox = true;
            ShowInTaskbar = true;
            ClientSize = new Size(820, 542);
            Font = new Font("Microsoft YaHei UI", 9f);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            _labelProxy = AddLabel(12, 12, 796);
            _labelApp = AddLabel(12, 36, 796);
            _labelConnections = AddLabel(12, 60, 796);
            _labelClash = AddLabel(12, 84, 796);
            _labelGuard = AddLabel(12, 108, 796);

            Label caption = AddLabel(12, 138, 796);
            caption.Text = "连接与规则命中（来自 Clash /connections，只显示 ChatGPT / codex 的活跃连接；右键可复制）";

            _list = new BufferedListView();
            _list.Location = new Point(12, 160);
            _list.Size = new Size(796, 296);
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.HideSelection = false;
            _list.MultiSelect = false;
            _list.Columns.Add("进程", 110);
            _list.Columns.Add("目标", 230);
            _list.Columns.Add("规则", 130);
            _list.Columns.Add("代理链", 170);
            _list.Columns.Add("上行", 72, HorizontalAlignment.Right);
            _list.Columns.Add("下行", 72, HorizontalAlignment.Right);
            _list.ContextMenuStrip = BuildListMenu();
            Controls.Add(_list);

            _labelStatus = AddLabel(12, 468, 796);
            _labelStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Button refresh = AddButton("刷新连接", 12, 494, 90);
            _buttonStart = AddButton("启动 ChatGPT", 110, 494, 118);
            _buttonRestart = AddButton("重启为代理", 236, 494, 108);
            _buttonClose = AddButton("关闭 ChatGPT", 352, 494, 108);
            Button diag = AddButton("生成诊断", 468, 494, 90);
            Button logs = AddButton("打开日志", 566, 494, 90);
            Button closePanel = AddButton("关闭面板", 718, 494, 90);
            closePanel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            refresh.Click += OnRefreshConnections;
            _buttonStart.Click += OnStart;
            _buttonRestart.Click += OnRestart;
            _buttonClose.Click += OnCloseApp;
            diag.Click += OnDiag;
            logs.Click += OnOpenLogs;
            closePanel.Click += OnClosePanel;

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;
            _timer.Start();

            UpdateFromContext();
            Shown += OnPanelShown;
            Log.Info("状态面板已打开");
        }

        private ContextMenuStrip BuildListMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("复制目标", null, delegate { CopySelectedColumn(1); });
            menu.Items.Add("复制规则", null, delegate { CopySelectedColumn(2); });
            menu.Items.Add("复制代理链", null, delegate { CopySelectedColumn(3); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("复制整行", null, delegate { CopySelectedRow(); });
            menu.Opening += delegate
            {
                bool hasSelection = _list.SelectedItems.Count > 0;
                for (int i = 0; i < menu.Items.Count; i++)
                {
                    if (!(menu.Items[i] is ToolStripSeparator)) menu.Items[i].Enabled = hasSelection;
                }
            };
            return menu;
        }

        private void CopySelectedColumn(int columnIndex)
        {
            if (_list.SelectedItems.Count == 0) return;
            ListViewItem item = _list.SelectedItems[0];
            if (columnIndex < 0 || columnIndex >= item.SubItems.Count) return;
            string text = item.SubItems[columnIndex].Text;
            if (!string.IsNullOrEmpty(text)) CopyText(text);
        }

        private void CopySelectedRow()
        {
            if (_list.SelectedItems.Count == 0) return;
            ListViewItem item = _list.SelectedItems[0];
            string[] parts = new string[item.SubItems.Count];
            for (int i = 0; i < item.SubItems.Count; i++) parts[i] = item.SubItems[i].Text;
            CopyText(string.Join("\t", parts));
        }

        private void CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text);
                _labelStatus.Text = "已复制到剪贴板: " + (text.Length > 60 ? text.Substring(0, 60) + "…" : text);
            }
            catch (Exception ex)
            {
                Log.Warn("写入剪贴板失败: " + ex.Message);
            }
        }

        private void OnPanelShown(object sender, EventArgs e)
        {
            UpdateFromContext();
            LoadConnections(true);
        }

        private Label AddLabel(int x, int y, int width)
        {
            Label label = new Label();
            label.Location = new Point(x, y);
            label.Size = new Size(width, 20);
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.Text = "";
            Controls.Add(label);
            return label;
        }

        private Button AddButton(string text, int x, int y, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(width, 30);
            button.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(button);
            return button;
        }

        private void OnTick(object sender, EventArgs e)
        {
            UpdateFromContext();
            LoadConnections(false);
        }

        private void UpdateFromContext()
        {
            StatusReport report = _context.CurrentReport;
            ClashState clash = _context.CurrentClash;
            AppConfig cfg = _context.Config;

            if (report == null)
            {
                _labelProxy.Text = "代理: 正在检测…";
                return;
            }

            string proxyUrl = report.Proxy.ProxyUrl.Length > 0 ? report.Proxy.ProxyUrl : cfg.BuildProxyUrl(cfg.ProxyPort);
            if (!report.Proxy.PortOpen)
            {
                _labelProxy.Text = "代理: " + proxyUrl + "  不可用（" + report.Proxy.Detail + "）";
            }
            else if (!report.Proxy.TunnelOk)
            {
                _labelProxy.Text = "代理: " + proxyUrl + "  端口通/验证失败（" + report.Proxy.Detail + "）";
            }
            else
            {
                _labelProxy.Text = "代理: " + proxyUrl + "  可用（" + report.Proxy.Detail + "）";
            }

            _labelApp.Text = "ChatGPT: " + report.AppStateText +
                (report.CodexPresent ? (report.CodexViaProxy ? "  ·  codex 已走代理" : "  ·  codex 存在但暂未走代理") : "");

            string connections = "连接: 经代理 " + report.ProxiedConnections + "  ·  本地回环 " + report.LoopbackConnections + "  ·  外部直连 " + report.ExternalConnections;
            if (report.HasLeak) connections += "   ⚠ " + string.Join(", ", report.DirectRemotes.ToArray());
            _labelConnections.Text = connections;

            if (!cfg.ClashEnabled)
            {
                _labelClash.Text = "Clash 联动: 已关闭（config.ini [clash] enabled=true 可开启）";
            }
            else if (clash == null)
            {
                _labelClash.Text = "Clash: 正在检测…";
            }
            else if (!clash.ApiAvailable)
            {
                _labelClash.Text = "Clash API: 不可用（" + clash.Error + "）";
            }
            else
            {
                string text = "Clash: " + clash.Version + "  ·  " + clash.ModeText;
                if (clash.TunEnabled) text += "  ·  TUN 开";
                if (clash.PrimaryGroupName.Length > 0) text += "  ·  " + clash.PrimaryGroupName + " → " + clash.FormatNodeWithDelay(clash.PrimaryGroupNow);
                text += "  ·  GLOBAL " + clash.GlobalNow;
                _labelClash.Text = text;
            }

            _labelGuard.Text = "守护: " + (cfg.GuardEnabled ? "开" : "关") +
                "  ·  自动接管: " + (cfg.GuardAutoTakeover ? "开" : "关") +
                "  ·  开机自启: " + (_context.AutoStartEnabled ? "开" : "关");

            bool busy = _context.ActionBusy;
            _buttonStart.Enabled = !busy && report.App != AppState.Proxied;
            _buttonRestart.Enabled = !busy && report.App != AppState.NotRunning;
            _buttonClose.Enabled = !busy && report.App != AppState.NotRunning;
        }

        private void LoadConnections(bool force)
        {
            AppConfig cfg = _context.Config;
            if (!cfg.ClashEnabled)
            {
                _loadingConnections = false;
                _lastConnectionsAt = DateTime.UtcNow;
                _list.Items.Clear();
                _labelStatus.Text = "Clash 联动已关闭（config.ini [clash] enabled=true 可查看连接与规则命中）";
                return;
            }
            if (_loadingConnections) return;
            if (!force && (DateTime.UtcNow - _lastConnectionsAt).TotalSeconds < 5) return;
            _loadingConnections = true;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = "";
                List<ClashConnection> all = new List<ClashConnection>();
                Dictionary<int, string> localPortMap = null;
                try
                {
                    all = ClashApi.GetConnections(cfg, out error);
                    if (all.Count > 0)
                    {
                        List<ProcEntry> procs = AppProcess.Query();
                        if (procs.Count > 0)
                        {
                            localPortMap = StatusEvaluator.BuildLocalPortProcessMap(procs, TcpTable.GetConnections());
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                bool posted = false;
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new MethodInvoker(delegate { ApplyConnections(all, localPortMap, error); }));
                        posted = true;
                    }
                }
                catch
                {
                }
                if (!posted) _loadingConnections = false;
            });
        }

        private static string BuildConnectionKey(ClashConnection connection)
        {
            if (!string.IsNullOrEmpty(connection.Id)) return connection.Id;
            return connection.Process + "|" + connection.SourcePort + "|" + connection.TargetText + "|" + connection.Start;
        }

        private void ApplyConnections(List<ClashConnection> all, Dictionary<int, string> localPortMap, string error)
        {
            _loadingConnections = false;
            _lastConnectionsAt = DateTime.UtcNow;

            string selectedKey = null;
            if (_list.SelectedItems.Count > 0)
            {
                selectedKey = _list.SelectedItems[0].Tag as string;
            }
            int topIndex = _list.TopItem != null ? _list.TopItem.Index : -1;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                int count = 0;
                ListViewItem restoredSelection = null;
                for (int i = 0; i < all.Count; i++)
                {
                    ClashConnection connection = all[i];
                    string processName = StatusEvaluator.ResolveConnectionProcess(connection, localPortMap);
                    if (string.IsNullOrEmpty(processName)) continue;
                    ListViewItem item = new ListViewItem(processName);
                    item.SubItems.Add(connection.TargetText);
                    item.SubItems.Add(connection.RuleText);
                    item.SubItems.Add(connection.ChainText);
                    item.SubItems.Add(FormatBytes(connection.Upload));
                    item.SubItems.Add(FormatBytes(connection.Download));
                    string key = BuildConnectionKey(connection);
                    item.Tag = key;
                    _list.Items.Add(item);
                    if (selectedKey != null && string.Equals(selectedKey, key, StringComparison.Ordinal))
                    {
                        restoredSelection = item;
                    }
                    count++;
                }

                if (restoredSelection != null)
                {
                    restoredSelection.Selected = true;
                    restoredSelection.Focused = true;
                }
                if (topIndex >= 0 && topIndex < _list.Items.Count)
                {
                    try { _list.TopItem = _list.Items[topIndex]; } catch { }
                }

                if (count == 0)
                {
                    _labelStatus.Text = string.IsNullOrEmpty(error)
                        ? "当前没有 ChatGPT / codex 的连接经过 Clash（未运行或还没联网）"
                        : "读取 Clash 连接失败: " + error;
                }
                else
                {
                    _labelStatus.Text = "命中 " + count + " 条连接  ·  更新于 " + DateTime.Now.ToString("HH:mm:ss");
                }

                if (count != _lastLoggedCount || !string.Equals(error, _lastLoggedError, StringComparison.Ordinal))
                {
                    _lastLoggedCount = count;
                    _lastLoggedError = error;
                    Log.Info("面板连接已更新: 命中 " + count + " 条" + (string.IsNullOrEmpty(error) ? "" : "，错误: " + error));
                }
            }
            finally
            {
                _list.EndUpdate();
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        private void OnRefreshConnections(object sender, EventArgs e)
        {
            LoadConnections(true);
        }

        private void OnStart(object sender, EventArgs e)
        {
            _labelStatus.Text = "正在启动 ChatGPT…";
            _context.PanelLaunch();
            UpdateFromContext();
        }

        private void OnRestart(object sender, EventArgs e)
        {
            _labelStatus.Text = "正在重启 ChatGPT 为代理…";
            _context.PanelRestart();
            UpdateFromContext();
        }

        private void OnCloseApp(object sender, EventArgs e)
        {
            _context.PanelClose();
            UpdateFromContext();
        }

        private void OnDiag(object sender, EventArgs e)
        {
            _labelStatus.Text = "正在生成诊断报告…";
            _context.PanelDiag();
        }

        private void OnOpenLogs(object sender, EventArgs e)
        {
            _context.PanelOpenLogs();
        }

        private void OnClosePanel(object sender, EventArgs e)
        {
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _context.NotifyPanelClosed();
            base.OnFormClosed(e);
        }
    }
}
