using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ChatGptProxyTray
{
    internal sealed class TrayContext : ApplicationContext
    {
        private static readonly Color ColorGreen = Color.FromArgb(46, 160, 67);
        private static readonly Color ColorYellow = Color.FromArgb(210, 153, 34);
        private static readonly Color ColorRed = Color.FromArgb(218, 54, 51);
        private static readonly Color ColorGray = Color.FromArgb(110, 118, 129);

        private AppConfig _cfg;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _itemProxy;
        private readonly ToolStripMenuItem _itemApp;
        private readonly ToolStripMenuItem _itemLaunch;
        private readonly ToolStripMenuItem _itemClose;
        private ToolStripMenuItem _clashMenu;
        private ToolStripMenuItem _clashStatusItem;
        private ToolStripMenuItem _clashModeItem;
        private ToolStripMenuItem _clashNodeItem;
        private ToolStripMenuItem _clashTunItem;
        private ToolStripMenuItem _clashAutoItem;
        private ToolStripMenuItem _modeRuleItem;
        private ToolStripMenuItem _modeGlobalItem;
        private ToolStripMenuItem _modeDirectItem;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _verifyTimer;
        private readonly Dictionary<int, Icon> _icons = new Dictionary<int, Icon>();

        private StatusReport _report;
        private ProxyProbeResult _probe;
        private DateTime _probeAt = DateTime.MinValue;
        private ClashState _clash;
        private DateTime _clashAt = DateTime.MinValue;
        private bool _autoLaunchArmed = true;
        private bool _busy;
        private bool _directNotified;
        private bool _guardAutoRunning;
        private bool _lastBalloonIsGuard;
        private DateTime _lastGuardAutoAt = DateTime.MinValue;
        private ToolStripMenuItem _itemTakeover;
        private ToolStripMenuItem _itemGuard;
        private ToolStripMenuItem _itemGuardAuto;
        private ToolStripMenuItem _itemAutoStart;
        private ToolStripMenuItem _itemDiag;
        private ToolStripMenuItem _itemPanel;
        private ToolStripMenuItem _itemReload;
        private StatusPanel _panel;
        private DateTime _configWriteTime = DateTime.MinValue;

        public TrayContext()
        {
            _cfg = AppConfig.Load();

            _menu = new ContextMenuStrip();
            _itemProxy = AddStatusItem("Clash：检测中…");
            _itemApp = AddStatusItem("ChatGPT：检测中…");
            _menu.Items.Add(new ToolStripSeparator());
            BuildClashMenu();
            _menu.Items.Add(new ToolStripSeparator());
            _itemLaunch = AddActionItem("启动 ChatGPT（走代理）", OnLaunch);
            _itemClose = AddActionItem("关闭 ChatGPT", OnCloseApp);
            _itemPanel = AddActionItem("打开状态面板", OnOpenPanel);
            _menu.Items.Add(new ToolStripSeparator());
            _itemTakeover = AddActionItem("立即接管为代理（重启 ChatGPT）", OnTakeover);
            _menu.Items.Add(new ToolStripSeparator());
            _itemGuard = AddActionItem("守护模式（发现直连时提示）", OnToggleGuard);
            _itemGuardAuto = AddActionItem("发现直连时自动接管（重启为代理）", OnToggleGuardAuto);
            _itemAutoStart = AddActionItem("开机自启（登录时启动托盘）", OnToggleAutoStart);
            _itemDiag = AddActionItem("生成诊断报告", OnDiag);
            _menu.Items.Add(new ToolStripSeparator());
            AddActionItem("重新检测 Clash", OnDetect);
            AddActionItem("打开配置文件", OnOpenConfig);
            _itemReload = AddActionItem("重新加载配置", OnReloadConfig);
            AddActionItem("打开日志目录", OnOpenLogs);
            _menu.Items.Add(new ToolStripSeparator());
            AddActionItem("退出", OnExit);

            _icon = new NotifyIcon();
            _icon.Icon = GetIcon(ColorGray);
            _icon.Text = "ChatGPT 代理助手";
            _icon.ContextMenuStrip = _menu;
            _icon.Visible = true;
            _icon.DoubleClick += OnDoubleClick;
            _icon.BalloonTipClicked += OnBalloonClicked;

            _verifyTimer = new System.Windows.Forms.Timer();
            _verifyTimer.Tick += OnVerifyTick;

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = Math.Max(1000, _cfg.RefreshSeconds * 1000);
            _timer.Tick += OnTimerTick;
            _timer.Start();

            InitializeOptions();
            RefreshClashState(true);
            RefreshStatus(false);
            Log.Info("托盘已启动，配置: " + _cfg.ConfigPath);
        }

        private ToolStripMenuItem AddStatusItem(string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Enabled = false;
            _menu.Items.Add(item);
            return item;
        }

        private ToolStripMenuItem AddActionItem(string text, EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text, null, handler);
            _menu.Items.Add(item);
            return item;
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_verifyTimer.Enabled) return;
            CheckConfigReload();
            RefreshClashState(false);
            RefreshStatus(false);
            ConsiderAutoLaunch(_report);
        }

        private void RefreshStatus(bool forceProbe)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                ProxyProbeResult probe = GetProbe(forceProbe);
                _report = StatusEvaluator.Evaluate(_cfg, probe);
                ApplyStatus(_report);
            }
            catch (Exception ex)
            {
                Log.Error("刷新状态失败", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        private ProxyProbeResult GetProbe(bool force)
        {
            int ttlSeconds = _probe != null && _probe.PortOpen ? 60 : 10;
            if (!force && _probe != null && (DateTime.UtcNow - _probeAt).TotalSeconds < ttlSeconds) return _probe;
            _probe = ClashProbe.Detect(_cfg);
            _probeAt = DateTime.UtcNow;
            return _probe;
        }

        private void ApplyStatus(StatusReport report)
        {
            _report = report;

            Color color;
            string tooltip;
            if (!report.Proxy.PortOpen)
            {
                color = ColorRed;
                tooltip = "Clash :" + report.Proxy.Port + " 不可用";
            }
            else if (report.HasLeak)
            {
                color = ColorRed;
                tooltip = "检测到疑似直连 " + report.DirectRemotes.Count + " 条";
            }
            else
            {
                switch (report.App)
                {
                    case AppState.Proxied:
                        color = ColorGreen;
                        tooltip = "ChatGPT 已走代理 :" + report.Proxy.Port;
                        break;
                    case AppState.ProxiedOtherPort:
                        color = ColorYellow;
                        tooltip = "ChatGPT 代理端口不一致";
                        break;
                    case AppState.Unproxied:
                        color = ColorYellow;
                        tooltip = "ChatGPT 直连中 · 右键重启";
                        break;
                    default:
                        color = ColorGray;
                        tooltip = "ChatGPT 未运行 · Clash :" + report.Proxy.Port;
                        break;
                }
            }
            SetIcon(color, tooltip);

            string proxyUrl = report.Proxy.ProxyUrl.Length > 0 ? report.Proxy.ProxyUrl : _cfg.BuildProxyUrl(report.Proxy.Port);
            if (!report.Proxy.PortOpen) _itemProxy.Text = "Clash：" + proxyUrl + "  不可用";
            else if (!report.Proxy.TunnelOk) _itemProxy.Text = "Clash：" + proxyUrl + "  端口通/验证失败";
            else _itemProxy.Text = "Clash：" + proxyUrl + "  可用";
            _itemApp.Text = "ChatGPT：" + report.AppStateText;

            if (report.App == AppState.Proxied)
            {
                _itemLaunch.Text = "ChatGPT 已走代理";
                _itemLaunch.Enabled = false;
            }
            else if (report.App == AppState.NotRunning)
            {
                _itemLaunch.Text = "启动 ChatGPT（走代理）";
                _itemLaunch.Enabled = true;
            }
            else
            {
                _itemLaunch.Text = "重启为代理";
                _itemLaunch.Enabled = true;
            }
            _itemClose.Enabled = report.App != AppState.NotRunning;
            UpdateClashMenu();
            UpdateGuardMenu(report);
            ConsiderGuard(report);
        }

        private void BuildClashMenu()
        {
            _clashMenu = new ToolStripMenuItem("Clash 联动");

            _clashStatusItem = new ToolStripMenuItem("检测中…");
            _clashStatusItem.Enabled = false;
            _clashMenu.DropDownItems.Add(_clashStatusItem);

            _clashModeItem = new ToolStripMenuItem("代理模式");
            _modeRuleItem = new ToolStripMenuItem("规则模式（rule）", null, OnModeRule);
            _modeGlobalItem = new ToolStripMenuItem("全局模式（global）", null, OnModeGlobal);
            _modeDirectItem = new ToolStripMenuItem("直连模式（direct）", null, OnModeDirect);
            _clashModeItem.DropDownItems.Add(_modeRuleItem);
            _clashModeItem.DropDownItems.Add(_modeGlobalItem);
            _clashModeItem.DropDownItems.Add(_modeDirectItem);
            _clashMenu.DropDownItems.Add(_clashModeItem);

            _clashNodeItem = new ToolStripMenuItem("选择节点");
            ToolStripMenuItem loading = new ToolStripMenuItem("（打开菜单时加载）");
            loading.Enabled = false;
            _clashNodeItem.DropDownItems.Add(loading);
            _clashMenu.DropDownItems.Add(_clashNodeItem);

            _clashMenu.DropDownItems.Add(new ToolStripSeparator());

            _clashTunItem = new ToolStripMenuItem("TUN 模式（接管系统全部流量）", null, OnToggleTun);
            _clashMenu.DropDownItems.Add(_clashTunItem);

            _clashAutoItem = new ToolStripMenuItem("Clash 就绪时自动启动 ChatGPT", null, OnToggleAutoLaunch);
            _clashMenu.DropDownItems.Add(_clashAutoItem);

            _clashMenu.DropDownOpening += OnClashMenuOpening;
            _menu.Items.Add(_clashMenu);
        }

        private void OnClashMenuOpening(object sender, EventArgs e)
        {
            if (!_cfg.ClashEnabled) return;
            RefreshClashState(true);
            RebuildNodeMenu();
            UpdateClashMenu();
        }

        private void RefreshClashState(bool force)
        {
            if (!_cfg.ClashEnabled) return;
            if (!force && _clash != null && (DateTime.UtcNow - _clashAt).TotalSeconds < 10) return;
            try
            {
                _clash = ClashApi.GetState(_cfg);
                _clashAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Log.Warn("读取 Clash 状态失败: " + ex.Message);
            }
        }

        private void UpdateClashMenu()
        {
            if (_clashMenu == null) return;
            if (!_cfg.ClashEnabled)
            {
                _clashMenu.Text = "Clash 联动（已关闭）";
                _clashStatusItem.Text = "在 config.ini 的 [clash] enabled=true 启用";
                _clashModeItem.Enabled = false;
                _clashNodeItem.Enabled = false;
                _clashTunItem.Enabled = false;
                _clashAutoItem.Enabled = false;
                return;
            }

            _clashMenu.Text = "Clash 联动";
            ClashState state = _clash;
            if (state == null)
            {
                _clashStatusItem.Text = "Clash：尚未检测";
            }
            else if (!state.ApiAvailable)
            {
                _clashStatusItem.Text = "Clash API：不可用（" + state.Error + "）";
            }
            else
            {
                string text = "Clash API：" + state.Version + " · " + state.ModeText;
                if (state.TunEnabled) text += " · TUN 已开";
                _clashStatusItem.Text = text;
            }

            bool available = state != null && state.ApiAvailable;
            _clashModeItem.Enabled = available;
            _clashNodeItem.Enabled = available && state.Groups.Count > 0;
            _clashTunItem.Enabled = available;
            _clashAutoItem.Enabled = available || (_report != null && _report.Proxy.PortOpen);

            _modeRuleItem.Checked = available && string.Equals(state.Mode, "rule", StringComparison.OrdinalIgnoreCase);
            _modeGlobalItem.Checked = available && string.Equals(state.Mode, "global", StringComparison.OrdinalIgnoreCase);
            _modeDirectItem.Checked = available && string.Equals(state.Mode, "direct", StringComparison.OrdinalIgnoreCase);

            _clashTunItem.Checked = available && state.TunEnabled;
            _clashAutoItem.Checked = _cfg.AutoLaunchOnClash;

            if (available && state.PrimaryGroupName.Length > 0)
            {
                _clashNodeItem.Text = "选择节点（" + state.PrimaryGroupName + " → " + state.PrimaryGroupNow + "）";
            }
            else
            {
                _clashNodeItem.Text = "选择节点";
            }
        }

        private void RebuildNodeMenu()
        {
            _clashNodeItem.DropDownItems.Clear();
            ClashState state = _clash;
            if (state == null || !state.ApiAvailable || state.Groups.Count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("（没有可切换的策略组）");
                empty.Enabled = false;
                _clashNodeItem.DropDownItems.Add(empty);
                return;
            }

            for (int i = 0; i < state.Groups.Count; i++)
            {
                ClashGroup group = state.Groups[i];
                ToolStripMenuItem groupItem = new ToolStripMenuItem(group.Name + " → " + group.Now);
                if (group.Options.Count == 0)
                {
                    ToolStripMenuItem empty = new ToolStripMenuItem("（无节点）");
                    empty.Enabled = false;
                    groupItem.DropDownItems.Add(empty);
                }
                for (int j = 0; j < group.Options.Count; j++)
                {
                    string option = group.Options[j];
                    ToolStripMenuItem optionItem = new ToolStripMenuItem(option);
                    optionItem.Checked = string.Equals(option, group.Now, StringComparison.Ordinal);
                    string groupName = group.Name;
                    string nodeName = option;
                    optionItem.Click += delegate { OnSelectNode(groupName, nodeName); };
                    groupItem.DropDownItems.Add(optionItem);
                }
                _clashNodeItem.DropDownItems.Add(groupItem);
            }
        }

        private void SetClashMode(string mode)
        {
            string error;
            if (!ClashApi.SetMode(_cfg, mode, out error))
            {
                ShowBalloon("切换 Clash 模式失败", error, ToolTipIcon.Error);
                return;
            }
            RefreshClashState(true);
            UpdateClashMenu();
            ShowBalloon("Clash 模式已切换", ClashApi.ModeToText(mode) + "（仅当前 Clash 会话生效，重启 Clash 后恢复配置）", ToolTipIcon.Info);
            if (mode == "global" && _clash != null && _clash.ApiAvailable &&
                (string.Equals(_clash.GlobalNow, "DIRECT", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_clash.GlobalNow, "REJECT", StringComparison.OrdinalIgnoreCase)))
            {
                ShowBalloon("全局模式提示", "GLOBAL 当前选择是 " + _clash.GlobalNow + "，全局模式会直连；请先在 Clash 里为 GLOBAL 选一个节点。", ToolTipIcon.Warning);
            }
            _probe = null;
            RefreshStatus(true);
        }

        private void OnModeRule(object sender, EventArgs e)
        {
            SetClashMode("rule");
        }

        private void OnModeGlobal(object sender, EventArgs e)
        {
            SetClashMode("global");
        }

        private void OnModeDirect(object sender, EventArgs e)
        {
            SetClashMode("direct");
        }

        private void OnSelectNode(string group, string node)
        {
            string error;
            if (!ClashApi.SelectNode(_cfg, group, node, out error))
            {
                ShowBalloon("切换节点失败", error, ToolTipIcon.Error);
                return;
            }
            RefreshClashState(true);
            UpdateClashMenu();
            ShowBalloon("Clash 节点已切换", group + " → " + node + "（仅当前 Clash 会话生效，重启 Clash 后恢复）", ToolTipIcon.Info);
        }

        private void OnToggleTun(object sender, EventArgs e)
        {
            if (_clash == null || !_clash.ApiAvailable)
            {
                ShowBalloon("Clash API 不可用", "请先启动 Clash，并确认 mihomo API 可访问。", ToolTipIcon.Error);
                return;
            }

            bool enable = !_clash.TunEnabled;
            if (enable)
            {
                DialogResult result = MessageBox.Show(
                    "开启 TUN 后 Clash 会用虚拟网卡接管系统全部流量（包括其他程序），需要 Clash 侧已配置好 TUN。\n\n确认开启？",
                    "开启 TUN 模式", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (result != DialogResult.OK) return;
            }

            string error;
            if (!ClashApi.SetTun(_cfg, enable, out error))
            {
                ShowBalloon("设置 TUN 失败", error, ToolTipIcon.Error);
                return;
            }
            RefreshClashState(true);
            UpdateClashMenu();
            ShowBalloon(enable ? "TUN 已开启" : "TUN 已关闭",
                enable ? "系统流量现在由 Clash TUN 接管（仅当前会话，重启 Clash 后恢复配置）。" : "已恢复为系统代理/规则模式（仅当前会话）。", ToolTipIcon.Info);
        }

        private void OnToggleAutoLaunch(object sender, EventArgs e)
        {
            _cfg.AutoLaunchOnClash = !_cfg.AutoLaunchOnClash;
            _cfg.SetValue("clash", "auto_launch", _cfg.AutoLaunchOnClash ? "true" : "false");
            _autoLaunchArmed = true;
            UpdateClashMenu();
            ShowBalloon("自动启动", _cfg.AutoLaunchOnClash ? "Clash 就绪且 ChatGPT 未运行时会自动启动它。" : "已关闭自动启动。", ToolTipIcon.Info);
        }

        private void ConsiderAutoLaunch(StatusReport report)
        {
            if (!_cfg.AutoLaunchOnClash)
            {
                _autoLaunchArmed = true;
                return;
            }
            if (report == null || !report.Proxy.PortOpen)
            {
                _autoLaunchArmed = true;
                return;
            }
            if (!_autoLaunchArmed) return;
            if (report.App != AppState.NotRunning) return;

            _autoLaunchArmed = false;
            Log.Info("Clash 就绪且 ChatGPT 未运行，自动启动 ChatGPT");
            StartLaunch(false);
        }

        private void InitializeOptions()
        {
            if (AutoStart.IsEnabled() && !AutoStart.MatchesCurrentExe())
            {
                string error;
                if (AutoStart.SetEnabled(true, out error)) Log.Info("已更新开机自启路径: " + AutoStart.CommandLine());
                else Log.Warn("更新开机自启路径失败: " + error);
            }
            _itemGuard.Checked = _cfg.GuardEnabled;
            _itemGuardAuto.Checked = _cfg.GuardAutoTakeover;
            _itemAutoStart.Checked = AutoStart.IsEnabled();
            _configWriteTime = GetConfigWriteTime();
        }

        // ---- 状态面板 ----

        internal StatusReport CurrentReport
        {
            get { return _report; }
        }

        internal ClashState CurrentClash
        {
            get { return _clash; }
        }

        internal AppConfig Config
        {
            get { return _cfg; }
        }

        internal void OpenPanel()
        {
            if (_panel != null && _panel.IsDisposed) _panel = null;
            if (_panel == null) _panel = new StatusPanel(this);
            if (!_panel.Visible) _panel.Show();
            if (_panel.WindowState == FormWindowState.Minimized) _panel.WindowState = FormWindowState.Normal;
            _panel.Activate();
        }

        internal void NotifyPanelClosed()
        {
            Log.Info("状态面板已关闭");
            if (_panel != null && !_panel.IsDisposed) _panel.Dispose();
            _panel = null;
        }

        internal void PanelLaunch() { StartLaunch(false); }

        internal void PanelRestart() { StartLaunch(true); }

        internal void PanelClose() { OnCloseApp(null, EventArgs.Empty); }

        internal void PanelDiag() { OnDiag(null, EventArgs.Empty); }

        internal void PanelOpenLogs() { OnOpenLogs(null, EventArgs.Empty); }

        private void OnOpenPanel(object sender, EventArgs e)
        {
            OpenPanel();
        }

        // ---- 配置热重载 ----

        private DateTime GetConfigWriteTime()
        {
            try
            {
                return File.Exists(_cfg.ConfigPath) ? File.GetLastWriteTimeUtc(_cfg.ConfigPath) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private void CheckConfigReload()
        {
            DateTime writeTime = GetConfigWriteTime();
            if (writeTime == DateTime.MinValue || writeTime == _configWriteTime) return;
            ReloadConfig();
        }

        private void OnReloadConfig(object sender, EventArgs e)
        {
            ReloadConfig();
        }

        private void ReloadConfig()
        {
            try
            {
                _cfg = AppConfig.Load();
                _configWriteTime = GetConfigWriteTime();
                _timer.Interval = Math.Max(1000, _cfg.RefreshSeconds * 1000);
                _probe = null;
                _clash = null;
                _clashAt = DateTime.MinValue;
                _autoLaunchArmed = true;
                _directNotified = false;
                InitializeOptions();
                RefreshClashState(true);
                RefreshStatus(true);
                Log.Info("配置已重新加载: " + _cfg.ConfigPath);
                ShowBalloon("配置已重新加载",
                    "代理 " + _cfg.BuildProxyUrl(_cfg.ProxyPort) + "，刷新间隔 " + _cfg.RefreshSeconds + " 秒。", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Error("重新加载配置失败", ex);
                ShowBalloon("重新加载配置失败", ex.Message, ToolTipIcon.Error);
            }
        }

        private void UpdateGuardMenu(StatusReport report)
        {
            if (_itemTakeover == null) return;
            _itemTakeover.Enabled = report != null &&
                (report.App == AppState.Unproxied || report.App == AppState.ProxiedOtherPort);
            _itemGuard.Checked = _cfg.GuardEnabled;
            _itemGuardAuto.Checked = _cfg.GuardAutoTakeover;
        }

        private void ConsiderGuard(StatusReport report)
        {
            if (report == null) return;
            if (report.App != AppState.Unproxied && report.App != AppState.ProxiedOtherPort)
            {
                _directNotified = false;
                return;
            }
            if (!_cfg.GuardEnabled || _directNotified || _guardAutoRunning) return;
            if (DateTime.UtcNow.Subtract(_lastGuardAutoAt).TotalSeconds < 30) return;

            _directNotified = true;
            Log.Info("守护模式: 检测到 ChatGPT 未走代理（" + report.App + "）");

            if (_cfg.GuardAutoTakeover)
            {
                _lastGuardAutoAt = DateTime.UtcNow;
                _guardAutoRunning = true;
                try
                {
                    ShowBalloon("守护模式：正在接管", "ChatGPT 未走代理，正在重启为代理…", ToolTipIcon.Warning);
                    StartLaunch(true);
                }
                finally
                {
                    _guardAutoRunning = false;
                }
                return;
            }

            _lastBalloonIsGuard = true;
            ShowBalloon("ChatGPT 未走代理",
                "检测到 ChatGPT 正在直连（可能从开始菜单启动）。点击此气泡即可重启为代理。",
                ToolTipIcon.Warning);
        }

        private void OnBalloonClicked(object sender, EventArgs e)
        {
            if (!_lastBalloonIsGuard) return;
            _lastBalloonIsGuard = false;
            if (_report == null || _report.App == AppState.NotRunning) return;
            Log.Info("用户点击气泡，执行接管");
            StartLaunch(true);
        }

        private void OnTakeover(object sender, EventArgs e)
        {
            if (_report == null || _report.App == AppState.NotRunning)
            {
                ShowBalloon("ChatGPT 未运行", "请直接点「启动 ChatGPT（走代理）」。", ToolTipIcon.Info);
                return;
            }
            StartLaunch(true);
        }

        private void OnToggleGuard(object sender, EventArgs e)
        {
            _cfg.GuardEnabled = !_cfg.GuardEnabled;
            _cfg.SetValue("guard", "enabled", _cfg.GuardEnabled ? "true" : "false");
            _directNotified = false;
            _itemGuard.Checked = _cfg.GuardEnabled;
            ShowBalloon("守护模式", _cfg.GuardEnabled
                ? "已开启：发现 ChatGPT 直连时会弹出提示，点击提示即可一键接管。"
                : "已关闭：不再提示 ChatGPT 直连。", ToolTipIcon.Info);
        }

        private void OnToggleGuardAuto(object sender, EventArgs e)
        {
            _cfg.GuardAutoTakeover = !_cfg.GuardAutoTakeover;
            _cfg.SetValue("guard", "auto_takeover", _cfg.GuardAutoTakeover ? "true" : "false");
            if (_cfg.GuardAutoTakeover && !_cfg.GuardEnabled)
            {
                _cfg.GuardEnabled = true;
                _cfg.SetValue("guard", "enabled", "true");
            }
            _directNotified = false;
            _itemGuardAuto.Checked = _cfg.GuardAutoTakeover;
            _itemGuard.Checked = _cfg.GuardEnabled;
            ShowBalloon("自动接管", _cfg.GuardAutoTakeover
                ? "已开启：发现直连会自动重启 ChatGPT 使其走代理（会中断当前会话）。"
                : "已关闭：改为只提示，不自动重启。", ToolTipIcon.Info);
        }

        private void OnToggleAutoStart(object sender, EventArgs e)
        {
            bool enable = !AutoStart.IsEnabled();
            string error;
            if (!AutoStart.SetEnabled(enable, out error))
            {
                ShowBalloon("设置开机自启失败", error, ToolTipIcon.Error);
                return;
            }
            _itemAutoStart.Checked = AutoStart.IsEnabled();
            ShowBalloon("开机自启", enable
                ? "已开启：登录 Windows 后自动启动托盘（HKCU Run）。"
                : "已关闭：不再随 Windows 启动。", ToolTipIcon.Info);
        }

        private void OnDiag(object sender, EventArgs e)
        {
            string error;
            string path = Diag.Collect(_cfg, out error);
            if (path == null)
            {
                ShowBalloon("生成诊断报告失败", error, ToolTipIcon.Error);
                return;
            }
            ShowBalloon("诊断报告已生成", Path.GetFileName(path), ToolTipIcon.Info);
            try
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Log.Warn("打开诊断报告失败: " + ex.Message);
            }
        }

        private void SetIcon(Color color, string tooltip)
        {
            _icon.Icon = GetIcon(color);
            if (tooltip == null) tooltip = "";
            if (tooltip.Length > 62) tooltip = tooltip.Substring(0, 62);
            _icon.Text = tooltip;
        }

        private Icon GetIcon(Color color)
        {
            int key = color.ToArgb();
            Icon cached;
            if (_icons.TryGetValue(key, out cached)) return cached;

            Bitmap bitmap = new Bitmap(32, 32);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(color)) graphics.FillEllipse(brush, 1, 1, 30, 30);
                using (Pen pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2f)) graphics.DrawEllipse(pen, 2, 2, 28, 28);
                using (Font font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    SizeF size = graphics.MeasureString("C", font);
                    graphics.DrawString("C", font, textBrush, (32f - size.Width) / 2f, (32f - size.Height) / 2f);
                }
            }

            IntPtr handle = bitmap.GetHicon();
            Icon icon;
            using (Icon temporary = Icon.FromHandle(handle))
            {
                icon = (Icon)temporary.Clone();
            }
            NativeMethods.DestroyIcon(handle);
            bitmap.Dispose();

            _icons[key] = icon;
            return icon;
        }

        private void OnLaunch(object sender, EventArgs e)
        {
            if (_report != null && _report.App == AppState.Proxied)
            {
                ShowBalloon("无需重复启动", "ChatGPT 已经在代理下运行。", ToolTipIcon.Info);
                return;
            }
            bool running = _report != null && _report.App != AppState.NotRunning;
            StartLaunch(running);
        }

        private void StartLaunch(bool restart)
        {
            ProxyProbeResult probe = GetProbe(true);
            if (!probe.PortOpen)
            {
                ShowBalloon("Clash 代理不可用", "请先启动 Clash，当前 " + _cfg.BuildProxyUrl(_cfg.ProxyPort) + " 未监听。", ToolTipIcon.Error);
                return;
            }

            try
            {
                List<ProcEntry> processes = AppProcess.Query();
                if (processes.Count > 0)
                {
                    if (!restart)
                    {
                        ShowBalloon("ChatGPT 已在运行", "当前没有走代理，请使用菜单「重启为代理」。", ToolTipIcon.Warning);
                        return;
                    }
                    Log.Info("重启前关闭 ChatGPT，进程数=" + processes.Count);
                    AppProcess.CloseAll(processes, 10000);
                    Thread.Sleep(800);
                }

                string executable;
                string arguments;
                int pid = Launcher.Launch(_cfg, probe.Port, out executable, out arguments);
                Log.Info("启动完成 pid=" + pid);
                BeginVerify();
                ShowBalloon("已启动 ChatGPT", "代理 " + probe.ProxyUrl + "，正在验证（约 " + _cfg.VerifySeconds + " 秒）…", ToolTipIcon.Info);
                RefreshStatus(true);
            }
            catch (Exception ex)
            {
                Log.Error("启动 ChatGPT 失败", ex);
                ShowBalloon("启动失败", ex.Message, ToolTipIcon.Error);
            }
        }

        private void BeginVerify()
        {
            _verifyTimer.Stop();
            _verifyTimer.Interval = Math.Max(3000, _cfg.VerifySeconds * 1000);
            _verifyTimer.Start();
        }

        private void OnVerifyTick(object sender, EventArgs e)
        {
            _verifyTimer.Stop();
            _probe = null;
            RefreshStatus(true);
            ShowVerificationBalloon(_report);
        }

        private void ShowVerificationBalloon(StatusReport report)
        {
            if (report == null) return;

            if (report.App == AppState.Proxied)
            {
                string message = "已走代理 " + report.Proxy.ProxyUrl + "，检测到 " + report.ProxiedConnections + " 条经代理连接。";
                if (report.CodexPresent)
                {
                    message += report.CodexViaProxy ? " codex 核心也已走代理。" : " codex 核心暂未检测到代理连接（可能还没联网）。";
                }
                if (report.HasLeak) message += " 但发现疑似直连: " + JoinRemotes(report);
                ShowBalloon("代理已生效", message, report.HasLeak ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            else if (report.App == AppState.ProxiedOtherPort)
            {
                ShowBalloon("代理端口不一致", "ChatGPT 启动在 " + report.DetectedProxyUrl + "，与当前 Clash 端口不一致，建议重启为代理。", ToolTipIcon.Warning);
            }
            else if (report.App == AppState.Unproxied)
            {
                ShowBalloon("代理未生效", "ChatGPT 命令行里没有 --proxy-server，可能被已有实例复用。请退出 ChatGPT 后重试。", ToolTipIcon.Error);
            }
            else
            {
                ShowBalloon("ChatGPT 未运行", "启动后没有检测到进程，请查看日志了解原因。", ToolTipIcon.Error);
            }
        }

        private static string JoinRemotes(StatusReport report)
        {
            return string.Join(", ", report.DirectRemotes.ToArray());
        }

        private void OnCloseApp(object sender, EventArgs e)
        {
            List<ProcEntry> processes = AppProcess.Query();
            if (processes.Count == 0)
            {
                ShowBalloon("ChatGPT 未运行", "没有需要关闭的进程。", ToolTipIcon.Info);
                return;
            }
            DialogResult confirm = MessageBox.Show(
                "确定要关闭 ChatGPT 吗？\n\n正在生成的内容和未发送的输入会丢失。",
                "关闭 ChatGPT", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.OK) return;
            AppProcess.CloseAll(processes, 10000);
            _probe = null;
            RefreshStatus(true);
            ShowBalloon("已关闭 ChatGPT", "进程已全部退出。", ToolTipIcon.Info);
        }

        private void OnDetect(object sender, EventArgs e)
        {
            RefreshClashState(true);
            _probe = null;
            RefreshStatus(true);
            if (_report == null) return;
            string message = _report.Proxy.PortOpen
                ? "端口 " + _report.Proxy.Port + " 已监听，CONNECT 验证：" + _report.Proxy.Detail
                : _report.Proxy.Detail;
            ShowBalloon("Clash 检测", message, _report.Proxy.PortOpen ? ToolTipIcon.Info : ToolTipIcon.Error);
        }

        private void OnOpenConfig(object sender, EventArgs e)
        {
            try
            {
                if (!File.Exists(_cfg.ConfigPath)) File.WriteAllText(_cfg.ConfigPath, "", Encoding.UTF8);
                Process.Start("notepad.exe", "\"" + _cfg.ConfigPath + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("打开配置文件失败", ex);
            }
        }

        private void OnOpenLogs(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Log.LogDirectory);
                Process.Start("explorer.exe", "\"" + Log.LogDirectory + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("打开日志目录失败", ex);
            }
        }

        private void OnDoubleClick(object sender, EventArgs e)
        {
            OpenPanel();
        }

        private void OnExit(object sender, EventArgs e)
        {
            ExitThread();
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.BalloonTipIcon = icon;
                _icon.ShowBalloonTip(5000);
            }
            catch (Exception ex)
            {
                Log.Warn("显示气泡失败: " + ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Dispose();
                }
                if (_verifyTimer != null)
                {
                    _verifyTimer.Stop();
                    _verifyTimer.Dispose();
                }
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                }
                if (_menu != null) _menu.Dispose();
                if (_panel != null)
                {
                    _panel.Dispose();
                    _panel = null;
                }
                foreach (Icon icon in _icons.Values) icon.Dispose();
                _icons.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
