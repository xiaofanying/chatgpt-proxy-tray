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
        private readonly TrayContext _context;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Label _labelProxy;
        private readonly Label _labelApp;
        private readonly Label _labelConnections;
        private readonly Label _labelClash;
        private readonly Label _labelGuard;
        private readonly Label _labelStatus;
        private readonly ListView _list;
        private readonly Button _buttonStart;
        private readonly Button _buttonRestart;
        private readonly Button _buttonClose;
        private DateTime _lastConnectionsAt = DateTime.MinValue;
        private bool _loadingConnections;

        public StatusPanel(TrayContext context)
        {
            _context = context;
            Text = "ChatGPT 代理助手 · 状态面板";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(820, 542);
            Font = new Font("Microsoft YaHei UI", 9f);

            _labelProxy = AddLabel(12, 12, 790);
            _labelApp = AddLabel(12, 36, 790);
            _labelConnections = AddLabel(12, 60, 790);
            _labelClash = AddLabel(12, 84, 790);
            _labelGuard = AddLabel(12, 108, 790);

            Label caption = AddLabel(12, 138, 790);
            caption.Text = "连接与规则命中（来自 Clash /connections，只显示 ChatGPT / codex 的活跃连接）";

            _list = new ListView();
            _list.Location = new Point(12, 160);
            _list.Size = new Size(796, 296);
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.HideSelection = false;
            _list.MultiSelect = false;
            _list.Columns.Add("进程", 100);
            _list.Columns.Add("目标", 230);
            _list.Columns.Add("规则", 130);
            _list.Columns.Add("代理链", 170);
            _list.Columns.Add("上行", 70, HorizontalAlignment.Right);
            _list.Columns.Add("下行", 70, HorizontalAlignment.Right);
            Controls.Add(_list);

            _labelStatus = AddLabel(12, 468, 520);

            Button refresh = AddButton("刷新连接", 12, 494, 90);
            _buttonStart = AddButton("启动 ChatGPT", 110, 494, 118);
            _buttonRestart = AddButton("重启为代理", 236, 494, 108);
            _buttonClose = AddButton("关闭 ChatGPT", 352, 494, 108);
            Button diag = AddButton("生成诊断", 468, 494, 90);
            Button logs = AddButton("打开日志", 566, 494, 90);
            Button closePanel = AddButton("关闭面板", 664, 494, 90);

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
            label.AutoSize = false;
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

            _labelProxy.Text = report.Proxy.PortOpen
                ? "代理: " + report.Proxy.ProxyUrl + "  可用（" + report.Proxy.Detail + "）"
                : "代理: " + (report.Proxy.ProxyUrl.Length > 0 ? report.Proxy.ProxyUrl : cfg.BuildProxyUrl(cfg.ProxyPort)) + "  不可用（" + report.Proxy.Detail + "）";

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
                _labelClash.Text = "Clash: 尚未检测";
            }
            else if (!clash.ApiAvailable)
            {
                _labelClash.Text = "Clash API: 不可用（" + clash.Error + "）";
            }
            else
            {
                string text = "Clash: " + clash.Version + "  ·  " + clash.ModeText;
                if (clash.TunEnabled) text += "  ·  TUN 开";
                if (clash.PrimaryGroupName.Length > 0) text += "  ·  " + clash.PrimaryGroupName + " → " + clash.PrimaryGroupNow;
                text += "  ·  GLOBAL " + clash.GlobalNow;
                _labelClash.Text = text;
            }

            _labelGuard.Text = "守护: " + (cfg.GuardEnabled ? "开" : "关") +
                "  ·  自动接管: " + (cfg.GuardAutoTakeover ? "开" : "关") +
                "  ·  开机自启: " + (AutoStart.IsEnabled() ? "开" : "关");

            _buttonStart.Enabled = report.App != AppState.Proxied;
            _buttonRestart.Enabled = report.App != AppState.NotRunning;
            _buttonClose.Enabled = report.App != AppState.NotRunning;
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
                try
                {
                    all = ClashApi.GetConnections(cfg, out error);
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
                        BeginInvoke(new MethodInvoker(delegate { ApplyConnections(all, error); }));
                        posted = true;
                    }
                }
                catch
                {
                }
                if (!posted) _loadingConnections = false;
            });
        }

        private void ApplyConnections(List<ClashConnection> all, string error)
        {
            _loadingConnections = false;
            _lastConnectionsAt = DateTime.UtcNow;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                int count = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    ClashConnection connection = all[i];
                    if (!IsAppProcess(connection.Process)) continue;
                    ListViewItem item = new ListViewItem(string.IsNullOrEmpty(connection.Process) ? "未知" : connection.Process);
                    item.SubItems.Add(connection.TargetText);
                    item.SubItems.Add(connection.RuleText);
                    item.SubItems.Add(connection.ChainText);
                    item.SubItems.Add(FormatBytes(connection.Upload));
                    item.SubItems.Add(FormatBytes(connection.Download));
                    _list.Items.Add(item);
                    count++;
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
                Log.Info("面板连接已更新: 命中 " + count + " 条" + (string.IsNullOrEmpty(error) ? "" : "，错误: " + error));
            }
            finally
            {
                _list.EndUpdate();
            }
        }

        private static bool IsAppProcess(string process)
        {
            if (string.IsNullOrEmpty(process)) return false;
            return process.IndexOf("chatgpt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   process.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        private void RunTrayAction(Action action, string description)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                _labelStatus.Text = description + "…";
                _labelStatus.Refresh();
                Application.DoEvents();
                action();
            }
            catch (Exception ex)
            {
                Log.Error("面板操作失败: " + description, ex);
                MessageBox.Show(ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                UpdateFromContext();
            }
        }

        private void OnRefreshConnections(object sender, EventArgs e)
        {
            LoadConnections(true);
        }

        private void OnStart(object sender, EventArgs e)
        {
            RunTrayAction(_context.PanelLaunch, "启动 ChatGPT");
        }

        private void OnRestart(object sender, EventArgs e)
        {
            RunTrayAction(_context.PanelRestart, "重启 ChatGPT");
        }

        private void OnCloseApp(object sender, EventArgs e)
        {
            RunTrayAction(_context.PanelClose, "关闭 ChatGPT");
        }

        private void OnDiag(object sender, EventArgs e)
        {
            RunTrayAction(_context.PanelDiag, "生成诊断报告");
        }

        private void OnOpenLogs(object sender, EventArgs e)
        {
            RunTrayAction(_context.PanelOpenLogs, "打开日志目录");
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
