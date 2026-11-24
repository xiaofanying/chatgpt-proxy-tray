using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ChatGptProxyTray
{
    internal sealed class TrayContext : ApplicationContext
    {
        public const string WakeupEventName = @"Local\ChatGptProxyTray.ShowPanel";

        private static readonly Color ColorGreen = Color.FromArgb(46, 160, 67);
        private static readonly Color ColorYellow = Color.FromArgb(210, 153, 34);
        private static readonly Color ColorRed = Color.FromArgb(218, 54, 51);
        private static readonly Color ColorGray = Color.FromArgb(110, 118, 129);

        private AppConfig _cfg;
        private readonly Control _invoker;
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

        private EventWaitHandle _wakeupEvent;
        private RegisteredWaitHandle _wakeupWaitHandle;

        private StatusReport _report;
        private ProxyProbeResult _probe;
        private DateTime _probeAt = DateTime.MinValue;
        private ClashState _clash;
        private DateTime _clashAt = DateTime.MinValue;
        private bool _autoLaunchArmed = true;
        private bool _autoStartEnabled;
        private bool _refreshingStatus;
        private bool _pendingStatusRefresh;
        private bool _pendingStatusForceProbe;
        private readonly List<Action<StatusReport>> _pendingStatusCallbacks = new List<Action<StatusReport>>();
        private bool _refreshingClash;
        private bool _pendingClashRefresh;
        private bool _pendingClashForce;
        private readonly List<Action> _pendingClashCallbacks = new List<Action>();
        private bool _actionBusy;
        private bool _directNotified;
        private bool _guardAutoRunning;
        private bool _lastBalloonIsGuard;
        private bool _disposed;
        private DateTime _lastGuardAutoAt = DateTime.MinValue;
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

            // 隐藏控件用于在任意后台线程安全地 BeginInvoke 回 UI 线程
            _invoker = new Control();
            IntPtr handle = _invoker.Handle;
            if (handle == IntPtr.Zero) Log.Warn("创建 UI 调度句柄失败");

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
            RegisterWakeupListener();
            RefreshClashStateAsync(true, null);
            RefreshStatusAsync(false, null);
            Log.Info("托盘已启动，配置: " + _cfg.ConfigPath);
        }

        private void RegisterWakeupListener()
        {
            try
            {
                bool createdNew;
                _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeupEventName, out createdNew);
                _wakeupWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                    _wakeupEvent,
                    delegate(object state, bool timedOut)
                    {
                        if (!timedOut)
                        {
                            PostUi(delegate { OpenPanel(); });
                        }
                    },
                    null,
                    -1,
                    false);
            }
            catch (Exception ex)
            {
                Log.Warn("注册单实例唤醒事件失败: " + ex.Message);
            }
        }

        public static bool SignalExistingInstance()
        {
            try
            {
                using (EventWaitHandle handle = EventWaitHandle.OpenExisting(WakeupEventName))
                {
                    return handle.Set();
                }
            }
            catch
            {
                return false;
            }
        }

        private void PostUi(Action action)
        {
            if (_disposed || action == null) return;
            try
            {
                if (_invoker != null && !_invoker.IsDisposed && _invoker.IsHandleCreated)
                {
                    _invoker.BeginInvoke(action);
                }
            }
            catch
            {
            }
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
            if (CheckConfigReload()) return;
            RefreshClashStateAsync(false, null);
            RefreshStatusAsync(false, null);
        }

        private void RefreshStatusAsync(bool forceProbe, Action<StatusReport> onCompleted)
        {
            if (_disposed) return;
            if (_refreshingStatus)
            {
                if (forceProbe || onCompleted != null)
                {
                    _pendingStatusRefresh = true;
                    if (forceProbe) _pendingStatusForceProbe = true;
                    if (onCompleted != null) _pendingStatusCallbacks.Add(onCompleted);
                }
                return;
            }
            _refreshingStatus = true;

            AppConfig cfg = _cfg;
            ProxyProbeResult cachedProbe = _probe;
            DateTime cachedProbeAt = _probeAt;

            ThreadPool.QueueUserWorkItem(delegate
            {
                ProxyProbeResult probe = cachedProbe;
                DateTime probeAt = cachedProbeAt;
                StatusReport report = null;
                try
                {
                    int ttlSeconds = probe != null && probe.PortOpen ? 60 : 10;
                    if (forceProbe || probe == null || (DateTime.UtcNow - probeAt).TotalSeconds >= ttlSeconds)
                    {
                        probe = ClashProbe.Detect(cfg);
                        probeAt = DateTime.UtcNow;
                    }
                    report = StatusEvaluator.Evaluate(cfg, probe);
                }
                catch (Exception ex)
                {
                    Log.Error("刷新状态失败", ex);
                }

                PostUi(delegate
                {
                    _refreshingStatus = false;
                    if (report != null)
                    {
                        _probe = probe;
                        _probeAt = probeAt;
                        ApplyStatus(report);
                        ConsiderAutoLaunch(report);
                    }
                    if (onCompleted != null) onCompleted(report);

                    if (_pendingStatusRefresh && !_disposed)
                    {
                        bool nextForce = _pendingStatusForceProbe;
                        List<Action<StatusReport>> callbacks = new List<Action<StatusReport>>(_pendingStatusCallbacks);
                        _pendingStatusRefresh = false;
                        _pendingStatusForceProbe = false;
                        _pendingStatusCallbacks.Clear();
                        RefreshStatusAsync(nextForce, delegate(StatusReport nextReport)
                        {
                            for (int i = 0; i < callbacks.Count; i++) callbacks[i](nextReport);
                        });
                    }
                });
            });
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
            else if (report.App == AppState.Proxied && report.HasLeak)
            {
                color = ColorRed;
                tooltip = "代理模式下检测到疑似直连 " + report.DirectRemotes.Count + " 条";
            }
            else if (!report.Proxy.TunnelOk)
            {
                color = ColorYellow;
                tooltip = "Clash :" + report.Proxy.Port + " 端口通但出网验证失败";
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
                _itemLaunch.Enabled = !_actionBusy;
            }
            else
            {
                _itemLaunch.Text = "重启为代理（接管当前实例）";
                _itemLaunch.Enabled = !_actionBusy;
            }
            _itemClose.Enabled = !_actionBusy && report.App != AppState.NotRunning;
            UpdateClashMenu();
            UpdateGuardMenu();
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
            ToolStripMenuItem loading = new ToolStripMenuItem("（正在加载…）");
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
            // 立即用当前缓存构建菜单避免阻塞展开，同时后台异步刷新最新状态
            bool hadGroups = _clash != null && _clash.ApiAvailable && _clash.Groups.Count > 0;
            RebuildNodeMenu();
            UpdateClashMenu();
            RefreshClashStateAsync(true, delegate
            {
                // 若用户已把鼠标移入「选择节点」子菜单且已有节点列表，避免清空导致子菜单被意外折叠
                if (!_clashNodeItem.DropDown.Visible || !hadGroups)
                {
                    RebuildNodeMenu();
                }
                UpdateClashMenu();
            });
        }

        private void RefreshClashStateAsync(bool force, Action onCompleted)
        {
            if (_disposed || !_cfg.ClashEnabled) return;
            if (!force && _clash != null && (DateTime.UtcNow - _clashAt).TotalSeconds < 10)
            {
                if (onCompleted != null) onCompleted();
                return;
            }
            if (_refreshingClash)
            {
                if (force || onCompleted != null)
                {
                    _pendingClashRefresh = true;
                    if (force) _pendingClashForce = true;
                    if (onCompleted != null) _pendingClashCallbacks.Add(onCompleted);
                }
                return;
            }
            _refreshingClash = true;

            AppConfig cfg = _cfg;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ClashState state = null;
                try
                {
                    state = ClashApi.GetState(cfg);
                }
                catch (Exception ex)
                {
                    Log.Warn("读取 Clash 状态失败: " + ex.Message);
                }

                PostUi(delegate
                {
                    _refreshingClash = false;
                    if (state != null)
                    {
                        _clash = state;
                        _clashAt = DateTime.UtcNow;
                        UpdateClashMenu();
                    }
                    if (onCompleted != null) onCompleted();

                    if (_pendingClashRefresh && !_disposed)
                    {
                        bool nextForce = _pendingClashForce;
                        List<Action> callbacks = new List<Action>(_pendingClashCallbacks);
                        _pendingClashRefresh = false;
                        _pendingClashForce = false;
                        _pendingClashCallbacks.Clear();
                        RefreshClashStateAsync(nextForce, delegate
                        {
                            for (int i = 0; i < callbacks.Count; i++) callbacks[i]();
                        });
                    }
                });
            });
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
                _clashStatusItem.Text = "Clash：检测中…";
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
                _clashNodeItem.Text = "选择节点（" + state.PrimaryGroupName + " → " + state.FormatNodeWithDelay(state.PrimaryGroupNow) + "）";
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
                ToolStripMenuItem groupItem = new ToolStripMenuItem(group.Name + " → " + state.FormatNodeWithDelay(group.Now));
                if (group.Options.Count == 0)
                {
                    ToolStripMenuItem empty = new ToolStripMenuItem("（无节点）");
                    empty.Enabled = false;
                    groupItem.DropDownItems.Add(empty);
                }
                for (int j = 0; j < group.Options.Count; j++)
                {
                    string option = group.Options[j];
                    ToolStripMenuItem optionItem = new ToolStripMenuItem(state.FormatNodeWithDelay(option));
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
            AppConfig cfg = _cfg;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                bool ok = ClashApi.SetMode(cfg, mode, out error);
                ClashState updated = ok ? ClashApi.GetState(cfg) : null;
                PostUi(delegate
                {
                    if (!ok)
                    {
                        ShowBalloon("切换 Clash 模式失败", error, ToolTipIcon.Error);
                        return;
                    }
                    if (updated != null)
                    {
                        _clash = updated;
                        _clashAt = DateTime.UtcNow;
                    }
                    UpdateClashMenu();
                    if (mode == "global" && _clash != null && _clash.ApiAvailable &&
                        (string.Equals(_clash.GlobalNow, "DIRECT", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(_clash.GlobalNow, "REJECT", StringComparison.OrdinalIgnoreCase)))
                    {
                        ShowBalloon("全局模式提示", "GLOBAL 当前选择是 " + _clash.GlobalNow + "，全局模式会直连；请先在 Clash 里为 GLOBAL 选一个节点。", ToolTipIcon.Warning);
                    }
                    else
                    {
                        ShowBalloon("Clash 模式已切换", ClashApi.ModeToText(mode) + "（仅当前 Clash 会话生效，重启 Clash 后恢复配置）", ToolTipIcon.Info);
                    }
                    _probe = null;
                    RefreshStatusAsync(true, null);
                });
            });
        }

        private void OnModeRule(object sender, EventArgs e) { SetClashMode("rule"); }
        private void OnModeGlobal(object sender, EventArgs e) { SetClashMode("global"); }
        private void OnModeDirect(object sender, EventArgs e) { SetClashMode("direct"); }

        private void OnSelectNode(string group, string node)
        {
            AppConfig cfg = _cfg;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                bool ok = ClashApi.SelectNode(cfg, group, node, out error);
                ClashState updated = ok ? ClashApi.GetState(cfg) : null;
                PostUi(delegate
                {
                    if (!ok)
                    {
                        ShowBalloon("切换节点失败", error, ToolTipIcon.Error);
                        return;
                    }
                    if (updated != null)
                    {
                        _clash = updated;
                        _clashAt = DateTime.UtcNow;
                    }
                    UpdateClashMenu();
                    ShowBalloon("Clash 节点已切换", group + " → " + node + "（仅当前 Clash 会话生效，重启 Clash 后恢复）", ToolTipIcon.Info);
                });
            });
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

            AppConfig cfg = _cfg;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                bool ok = ClashApi.SetTun(cfg, enable, out error);
                ClashState updated = ok ? ClashApi.GetState(cfg) : null;
                PostUi(delegate
                {
                    if (!ok)
                    {
                        ShowBalloon("设置 TUN 失败", error, ToolTipIcon.Error);
                        return;
                    }
                    if (updated != null)
                    {
                        _clash = updated;
                        _clashAt = DateTime.UtcNow;
                    }
                    UpdateClashMenu();
                    ShowBalloon(enable ? "TUN 已开启" : "TUN 已关闭",
                        enable ? "系统流量现在由 Clash TUN 接管（仅当前会话，重启 Clash 后恢复配置）。" : "已恢复为系统代理/规则模式（仅当前会话）。", ToolTipIcon.Info);
                });
            });
        }

        private void OnToggleAutoLaunch(object sender, EventArgs e)
        {
            _cfg.AutoLaunchOnClash = !_cfg.AutoLaunchOnClash;
            SaveConfigValue("clash", "auto_launch", _cfg.AutoLaunchOnClash ? "true" : "false");
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
            if (!_autoLaunchArmed || _actionBusy) return;
            if (report.App != AppState.NotRunning) return;

            _autoLaunchArmed = false;
            Log.Info("Clash 就绪且 ChatGPT 未运行，自动启动 ChatGPT");
            StartLaunchAsync(false);
        }

        private void InitializeOptions()
        {
            _autoStartEnabled = AutoStart.IsEnabled();
            if (_autoStartEnabled && !AutoStart.MatchesCurrentExe())
            {
                string error;
                if (AutoStart.SetEnabled(true, out error)) Log.Info("已更新开机自启路径: " + AutoStart.CommandLine());
                else Log.Warn("更新开机自启路径失败: " + error);
                _autoStartEnabled = AutoStart.IsEnabled();
            }
            _itemGuard.Checked = _cfg.GuardEnabled;
            _itemGuardAuto.Checked = _cfg.GuardAutoTakeover;
            _itemAutoStart.Checked = _autoStartEnabled;
            _configWriteTime = GetConfigWriteTime();
        }

        private void SaveConfigValue(string section, string key, string value)
        {
            if (_cfg.SetValue(section, key, value))
            {
                // 同步更新时间戳，防止自身写配置在下一次定时器 tick 被误判为外部改动而重复重载
                _configWriteTime = GetConfigWriteTime();
            }
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

        internal bool AutoStartEnabled
        {
            get { return _autoStartEnabled; }
        }

        internal bool ActionBusy
        {
            get { return _actionBusy; }
        }

        internal void OpenPanel()
        {
            if (_panel != null && _panel.IsDisposed) _panel = null;
            if (_panel == null) _panel = new StatusPanel(this);
            if (!_panel.Visible) _panel.Show();
            if (_panel.WindowState == FormWindowState.Minimized) _panel.WindowState = FormWindowState.Normal;
            _panel.BringToFront();
            _panel.Activate();
            try
            {
                NativeMethods.SetForegroundWindow(_panel.Handle);
            }
            catch
            {
            }
        }

        internal void NotifyPanelClosed()
        {
            Log.Info("状态面板已关闭");
            if (_panel != null && !_panel.IsDisposed) _panel.Dispose();
            _panel = null;
        }

        internal void PanelLaunch() { StartLaunchAsync(false); }

        internal void PanelRestart() { StartLaunchAsync(true); }

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

        private bool CheckConfigReload()
        {
            DateTime writeTime = GetConfigWriteTime();
            if (writeTime == DateTime.MinValue || writeTime == _configWriteTime) return false;
            ReloadConfig();
            return true;
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
                RefreshClashStateAsync(true, null);
                RefreshStatusAsync(true, null);
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

        private void UpdateGuardMenu()
        {
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
            // Clash 代理尚未监听时不锁死 _directNotified，等 Clash 启动就绪后再提示/接管
            if (!report.Proxy.PortOpen) return;
            if (!_cfg.GuardEnabled || _directNotified || _guardAutoRunning || _actionBusy) return;
            if (DateTime.UtcNow.Subtract(_lastGuardAutoAt).TotalSeconds < 30) return;

            _directNotified = true;
            Log.Info("守护模式: 检测到 ChatGPT 未走代理（" + report.App + "）");

            if (_cfg.GuardAutoTakeover)
            {
                _lastGuardAutoAt = DateTime.UtcNow;
                _guardAutoRunning = true;
                ShowBalloon("守护模式：正在接管", "ChatGPT 未走代理，正在重启为代理…", ToolTipIcon.Warning);
                StartLaunchAsync(true, delegate { _guardAutoRunning = false; });
                return;
            }

            ShowBalloon("ChatGPT 未走代理",
                "检测到 ChatGPT 正在直连（可能从开始菜单启动）。点击此气泡即可重启为代理。",
                ToolTipIcon.Warning, true);
        }

        private void OnBalloonClicked(object sender, EventArgs e)
        {
            if (!_lastBalloonIsGuard) return;
            _lastBalloonIsGuard = false;
            if (_report == null || _report.App == AppState.NotRunning) return;
            Log.Info("用户点击气泡，执行接管");
            StartLaunchAsync(true);
        }

        private void OnToggleGuard(object sender, EventArgs e)
        {
            _cfg.GuardEnabled = !_cfg.GuardEnabled;
            SaveConfigValue("guard", "enabled", _cfg.GuardEnabled ? "true" : "false");
            _directNotified = false;
            _itemGuard.Checked = _cfg.GuardEnabled;
            ShowBalloon("守护模式", _cfg.GuardEnabled
                ? "已开启：发现 ChatGPT 直连时会弹出提示，点击提示即可一键接管。"
                : "已关闭：不再提示 ChatGPT 直连。", ToolTipIcon.Info);
        }

        private void OnToggleGuardAuto(object sender, EventArgs e)
        {
            _cfg.GuardAutoTakeover = !_cfg.GuardAutoTakeover;
            SaveConfigValue("guard", "auto_takeover", _cfg.GuardAutoTakeover ? "true" : "false");
            if (_cfg.GuardAutoTakeover && !_cfg.GuardEnabled)
            {
                _cfg.GuardEnabled = true;
                SaveConfigValue("guard", "enabled", "true");
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
            bool enable = !_autoStartEnabled;
            string error;
            if (!AutoStart.SetEnabled(enable, out error))
            {
                ShowBalloon("设置开机自启失败", error, ToolTipIcon.Error);
                return;
            }
            _autoStartEnabled = AutoStart.IsEnabled();
            _itemAutoStart.Checked = _autoStartEnabled;
            ShowBalloon("开机自启", enable
                ? "已开启：登录 Windows 后自动启动托盘（HKCU Run）。"
                : "已关闭：不再随 Windows 启动。", ToolTipIcon.Info);
        }

        private void OnDiag(object sender, EventArgs e)
        {
            AppConfig cfg = _cfg;
            ShowBalloon("正在生成诊断报告", "正在收集代理、进程与连接信息…", ToolTipIcon.Info);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                string path = Diag.Collect(cfg, out error);
                PostUi(delegate
                {
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
                });
            });
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
            StartLaunchAsync(running);
        }

        private void StartLaunchAsync(bool restart)
        {
            StartLaunchAsync(restart, null);
        }

        private void StartLaunchAsync(bool restart, Action onFinished)
        {
            if (_actionBusy)
            {
                if (onFinished != null) onFinished();
                return;
            }
            _actionBusy = true;
            UpdateActionButtons();

            AppConfig cfg = _cfg;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ProxyProbeResult probe = null;
                string launchedProxyUrl = "";
                string errorTitle = "";
                string errorMessage = "";
                ToolTipIcon errorIcon = ToolTipIcon.Error;
                bool launched = false;

                try
                {
                    probe = ClashProbe.Detect(cfg);
                    if (!probe.PortOpen)
                    {
                        errorTitle = "Clash 代理不可用";
                        errorMessage = "请先启动 Clash，当前 " + cfg.BuildProxyUrl(cfg.ProxyPort) + " 未监听。";
                    }
                    else
                    {
                        List<ProcEntry> processes = AppProcess.FilterLifecycleProcesses(AppProcess.Query());
                        if (processes.Count > 0)
                        {
                            if (!restart)
                            {
                                errorTitle = "ChatGPT 已在运行";
                                errorMessage = "当前没有走代理，请使用菜单「重启为代理」。";
                                errorIcon = ToolTipIcon.Warning;
                            }
                            else
                            {
                                Log.Info("重启前关闭 ChatGPT，进程数=" + processes.Count);
                                AppProcess.CloseAll(processes, 10000);
                                Thread.Sleep(500);
                            }
                        }

                        if (errorTitle.Length == 0)
                        {
                            string executable;
                            string arguments;
                            int pid = Launcher.Launch(cfg, probe.Port, out executable, out arguments);
                            Log.Info("启动完成 pid=" + pid);
                            launchedProxyUrl = probe.ProxyUrl;
                            launched = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("启动 ChatGPT 失败", ex);
                    errorTitle = "启动失败";
                    errorMessage = ex.Message;
                }

                PostUi(delegate
                {
                    _actionBusy = false;
                    if (probe != null)
                    {
                        _probe = probe;
                        _probeAt = DateTime.UtcNow;
                    }
                    UpdateActionButtons();
                    if (launched)
                    {
                        BeginVerify();
                        ShowBalloon("已启动 ChatGPT", "代理 " + launchedProxyUrl + "，正在验证（约 " + _cfg.VerifySeconds + " 秒）…", ToolTipIcon.Info);
                        RefreshStatusAsync(false, null);
                    }
                    else if (errorTitle.Length > 0)
                    {
                        ShowBalloon(errorTitle, errorMessage, errorIcon);
                    }
                    if (onFinished != null) onFinished();
                });
            });
        }

        private void UpdateActionButtons()
        {
            if (_report == null) return;
            if (_report.App == AppState.Proxied)
            {
                _itemLaunch.Enabled = false;
            }
            else
            {
                _itemLaunch.Enabled = !_actionBusy;
            }
            _itemClose.Enabled = !_actionBusy && _report.App != AppState.NotRunning;
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
            RefreshStatusAsync(true, delegate(StatusReport report)
            {
                ShowVerificationBalloon(report);
            });
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
            if (_actionBusy) return;
            List<ProcEntry> processes = AppProcess.FilterLifecycleProcesses(AppProcess.Query());
            if (processes.Count == 0)
            {
                ShowBalloon("ChatGPT 未运行", "没有需要关闭的进程。", ToolTipIcon.Info);
                return;
            }
            DialogResult confirm = MessageBox.Show(
                "确定要关闭 ChatGPT 吗？\n\n正在生成的内容和未发送的输入会丢失。",
                "关闭 ChatGPT", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.OK) return;

            // 用户显式手动关闭 ChatGPT 后，解除本轮自动启动触发，避免 3 秒后被自动拉起
            _autoLaunchArmed = false;
            _actionBusy = true;
            UpdateActionButtons();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try
                {
                    ok = AppProcess.CloseAll(processes, 10000);
                }
                catch (Exception ex)
                {
                    Log.Error("关闭 ChatGPT 失败", ex);
                }

                PostUi(delegate
                {
                    _actionBusy = false;
                    UpdateActionButtons();
                    _probe = null;
                    RefreshStatusAsync(true, null);
                    ShowBalloon(ok ? "已关闭 ChatGPT" : "关闭未完全结束",
                        ok ? "进程已全部退出。" : "仍有部分残留进程，请查看日志。",
                        ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
                });
            });
        }

        private void OnDetect(object sender, EventArgs e)
        {
            RefreshClashStateAsync(true, null);
            _probe = null;
            RefreshStatusAsync(true, delegate(StatusReport report)
            {
                if (report == null) return;
                string message = report.Proxy.PortOpen
                    ? "端口 " + report.Proxy.Port + " 已监听，CONNECT 验证：" + report.Proxy.Detail
                    : report.Proxy.Detail;
                ToolTipIcon icon = !report.Proxy.PortOpen
                    ? ToolTipIcon.Error
                    : (report.Proxy.TunnelOk ? ToolTipIcon.Info : ToolTipIcon.Warning);
                ShowBalloon("Clash 检测", message, icon);
            });
        }

        private void OnOpenConfig(object sender, EventArgs e)
        {
            try
            {
                if (!File.Exists(_cfg.ConfigPath)) AppConfig.Load();
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
            ShowBalloon(title, text, icon, false);
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon, bool isGuard)
        {
            _lastBalloonIsGuard = isGuard;
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
                _disposed = true;
                if (_wakeupWaitHandle != null)
                {
                    try { _wakeupWaitHandle.Unregister(null); } catch { }
                    _wakeupWaitHandle = null;
                }
                if (_wakeupEvent != null)
                {
                    try { _wakeupEvent.Close(); } catch { }
                    _wakeupEvent = null;
                }
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
                if (_invoker != null) _invoker.Dispose();
                foreach (Icon icon in _icons.Values) icon.Dispose();
                _icons.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
