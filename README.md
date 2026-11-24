# ChatGPT 代理助手（ChatGptProxyTray）

![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)

一个 Windows 托盘小工具：**只让 ChatGPT 桌面版（MSIX 应用 `OpenAI.Codex`）的网络走 Clash，不改系统代理、不需要 TUN、不需要管理员权限。**

它通过「注入 Chromium 代理参数 + 代理环境变量」的方式启动 ChatGPT，所以：

- 只影响由本工具启动的 ChatGPT 实例，其他程序完全不受影响；
- 企业 VPN / 系统代理设置零冲突；
- 托盘实时显示代理是否生效，并能检测「疑似直连」的连接。

## 环境要求

- Windows 10/11 x64
- 已安装 ChatGPT 桌面版（Microsoft Store 版，包名 `OpenAI.Codex`）
- 本机已运行 Clash / Clash Verge / Mihomo 等本地 HTTP 代理（默认混合端口 `127.0.0.1:7897`）
- .NET Framework 4.8（Windows 10 1803+ 自带）
- 编译只需要系统自带的 `csc.exe`，无需安装 .NET SDK / Visual Studio

## 快速开始

```powershell
# 1. 编译（生成 dist\ChatGptProxyTray.exe）
powershell -ExecutionPolicy Bypass -File .\build.ps1

# 2. 运行
.\dist\ChatGptProxyTray.exe

# 3. 冒烟测试（可选，会启动/关闭一次 ChatGPT）
powershell -ExecutionPolicy Bypass -File .\test.ps1
```

启动后托盘出现图标，右键菜单：

| 菜单项 | 作用 |
| --- | --- |
| Clash：`http://127.0.0.1:7897` 可用 | 当前代理探测状态（含 CONNECT 出网验证） |
| ChatGPT：已走代理（N 条经代理连接） | 当前应用状态 |
| 启动 / 重启为代理（一个按钮智能切换） | ChatGPT 未运行时显示「启动 ChatGPT（走代理）」；检测到直连运行时自动变成「重启为代理」，点一下就关闭后以代理方式重启；已走代理时显示「ChatGPT 已走代理」且不可点 |
| 关闭 ChatGPT | 关闭 ChatGPT 全部相关进程（会弹确认框，避免误点丢失正在生成的内容） |
| 打开状态面板 | 双击托盘图标（单击不响应，避免误触）或点此打开：状态、连接与规则命中小窗 |
| 立即接管为代理（重启 ChatGPT） | 检测到直连/端口不一致时可用：关闭后以代理方式重启 |
| 守护模式（发现直连时提示） | 勾选开关；ChatGPT 直连时弹气泡，点击气泡即接管 |
| 发现直连时自动接管（重启为代理） | 勾选开关（默认关）；发现直连自动重启为代理，会中断当前会话 |
| 开机自启（登录时启动托盘） | 勾选开关，写 `HKCU\...\Run`；不需要管理员 |
| 生成诊断报告 | 生成 `diag\diag-*.txt` 并打开所在文件夹 |
| Clash 联动 → 状态行 | mihomo 版本 / 代理模式 / TUN 状态（10 秒缓存） |
| Clash 联动 → 代理模式 | 规则 / 全局 / 直连，点击即切换（带勾选） |
| Clash 联动 → 选择节点 | 按策略组展开，勾选当前节点，点击即切换 |
| Clash 联动 → TUN 模式 | 勾选开关；开启前会确认（会接管系统全部流量） |
| Clash 联动 → Clash 就绪时自动启动 ChatGPT | 勾选开关，写回 `config.ini`，默认关 |
| 重新检测 Clash | 强制刷新 Clash 状态并重新探测代理端口和出网 |
| 重新加载配置 | 手动重读 `config.ini`（托盘平时也会自动检测文件修改并热重载） |
| 打开配置文件 / 打开日志目录 | 打开 `config.ini` / `logs\` |
| 退出 | 退出托盘 |

图标颜色：**绿** = ChatGPT 已走代理；**黄** = ChatGPT 直连/端口不一致；**红** = 代理不可用或检测到疑似直连；**灰** = ChatGPT 未运行。

启动后约 8 秒会弹气泡报告验证结果（是否注入成功、有几条经代理连接、codex 核心是否继承、有无直连泄漏）。

## 命令行（便于脚本/排障）

```text
ChatGptProxyTray.exe                      启动托盘（默认）
ChatGptProxyTray.exe --status             打印代理/ChatGPT/连接状态
ChatGptProxyTray.exe --selftest           自检（配置/目标程序/代理/进程/TCP 表）
ChatGptProxyTray.exe --launch             启动 ChatGPT 并注入代理（已在运行返回 3）
ChatGptProxyTray.exe --launch --restart   先关闭再启动
ChatGptProxyTray.exe --close              关闭 ChatGPT
ChatGptProxyTray.exe --clash              打印 Clash 状态（版本/模式/TUN/策略组/当前节点）
ChatGptProxyTray.exe --clash-mode rule|global|direct   切换代理模式
ChatGptProxyTray.exe --clash-node <策略组> <节点>      切换策略组节点
ChatGptProxyTray.exe --clash-tun on|off   开关 TUN（全局接管，慎用）
ChatGptProxyTray.exe --clash-auto on|off  开关「Clash 就绪时自动启动 ChatGPT」
ChatGptProxyTray.exe --guard on|off       开关守护模式（发现直连时提示）
ChatGptProxyTray.exe --guard-auto on|off  发现直连时自动重启为代理（默认关）
ChatGptProxyTray.exe --autostart on|off   开机自启（HKCU Run）
ChatGptProxyTray.exe --diag               生成诊断报告到 diag\diag-*.txt
ChatGptProxyTray.exe --connections        打印进程连接与 Clash 规则/节点命中
ChatGptProxyTray.exe --panel              启动托盘并打开状态面板
ChatGptProxyTray.exe --help               帮助
```

结果同时写入程序目录的 `last-result.txt`；退出码：`0` 成功、`1` 失败、`2` 参数错误、`3` 已在运行、`4` 启动失败。

## 工作原理

1. **定位目标程序**：商店版 ChatGPT 的真实程序在 `C:\Program Files\WindowsApps\OpenAI.Codex_*\app\ChatGPT.exe`，但安装目录只读且直接 `CreateProcess` 会 `Access Denied`。本工具改为启动 **App Execution Alias**：
   `%LOCALAPPDATA%\Microsoft\WindowsApps\chatgpt-desktop.exe`（保留 MSIX 包身份，Windows 会把它路由到真实程序）。
2. **注入代理**：以 `UseShellExecute=false` 的 `CreateProcess` 方式启动，命令行附加
   `--proxy-server=http://127.0.0.1:7897 --proxy-bypass-list=localhost;127.0.0.1;[::1];<local> --disable-quic`
   （Chromium 网络栈强制走代理；禁用 QUIC，防止 UDP/HTTP3 绕过代理）。
   同时注入环境变量 `HTTP_PROXY / HTTPS_PROXY / ALL_PROXY / NO_PROXY`（大小写各一套）+ `NODE_USE_ENV_PROXY=1`，让子进程（如 `codex.exe`、`codex-command-runner.exe`）也继承代理。
   `NO_PROXY=127.0.0.1,localhost,::1` 必须保留 —— 否则本地网关（如 `127.0.0.1:3425`）也会被塞进代理，导致登录/推理接口失败。
3. **状态检测**：
   - WMI 查 `ChatGPT.exe / codex.exe / codex-command-runner.exe` 的 `CommandLine`，从根进程命令行提取 `--proxy-server=` 与当前 Clash 端口比对；
   - `GetExtendedTcpTable` 读内核 TCP 连接表：远端是 `127.0.0.1:<代理端口>` 计入「经代理连接」，其他回环端口是本地服务，非回环远端即「疑似直连」；
   - `codex.exe` 是否也有到代理端口的连接，用来确认核心进程继承了环境变量。
4. **关闭**：枚举属于 ChatGPT 相关进程的所有可见顶层窗口逐个 `PostMessage(WM_CLOSE)`（Electron 有多个窗口，只关主窗口不够）；一旦「根进程退出」或「可见窗口全部消失」，再等 2 秒收尾就强制清理残留子进程——实测关闭耗时从约 11 秒降到约 3 秒。
5. **Clash 联动**：Clash Verge 的 mihomo 内核通过命名管道 `\\.\pipe\verge-mihomo-production-*` 暴露 REST API（不是 HTTP 端口）。程序运行时枚举 `\\.\pipe\` 自动发现管道（Clash 重启后名字会变），读取 `/configs`、`/proxies`，并可：
   - 切换代理模式（`PATCH /configs`）；
   - 切换策略组节点（`PUT /proxies/{组名}`）；
   - 开关 TUN（`PATCH /configs {"tun":{"enable":...}}`，会接管系统全部流量，开启前弹确认）；
   - Clash 就绪时自动拉起 ChatGPT（需在菜单里打开）。
   响应是 chunked 编码，先拆块再解析；开关 TUN 时 mihomo 会重建监听器导致响应丢失，所以状态一律以「改动后重新读取的实际值」为准。
6. **守护模式**：托盘每次刷新状态时，如果发现 ChatGPT 在运行但命令行里没有当前代理参数（直连，或代理端口不一致），就弹气泡提示；点击气泡或菜单「立即接管为代理」会关闭并以代理方式重启。也可以在菜单/配置里打开「自动接管」（默认关，带 30 秒冷却，避免反复重启）。
7. **开机自启**：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `ChatGptProxyTray` 值（当前 exe 路径），不需要管理员；托盘启动时如果发现记录的是旧路径（比如移动了目录），会自动更新。
8. **诊断报告**：`--diag` 或菜单「生成诊断报告」把配置、代理探测、进程树、TCP 连接、Clash 状态、日志尾部写成一个文本文件到程序目录的 `diag\`。
9. **状态面板**：双击托盘图标（或菜单「打开状态面板」、`--panel`）打开 820×542 的小窗，实时显示代理、ChatGPT、连接统计、Clash 模式/节点和守护开关；并通过 mihomo 的 `/connections` 列出 ChatGPT / codex 每条活跃连接命中的规则、代理链和上下行字节。面板里也能直接刷新、启动/重启/关闭 ChatGPT、生成诊断、打开日志。
10. **连接与命中查看**：`--connections` 在命令行输出同样两份数据——进程自己到 `127.0.0.1:7897` 的 ESTABLISHED 连接（区分「经代理 / 本地 / 直连!」），以及 Clash 侧每条连接的「规则 + 代理链」，用来确认流量到底走了哪个节点。
11. **配置热重载**：托盘每次刷新时检查 `config.ini` 的修改时间，变了就重新加载（代理端口、守护、Clash、UI 段全部生效），菜单里也有「重新加载配置」手动触发；改配置不需要重启托盘。

## 配置（config.ini）

程序目录下的 `config.ini`，缺失会自动生成：

```ini
[proxy]
host=127.0.0.1
port=7897
fallback_ports=7897,7890,7891,10808,10809,1080   ; 配置端口不通时依次尝试
bypass_list=localhost;127.0.0.1;[::1];<local>    ; Chromium --proxy-bypass-list
no_proxy=127.0.0.1,localhost,::1                  ; 子进程 NO_PROXY
verify_connect=true                               ; 启动前用 CONNECT 验证出网

[target]
package_name=OpenAI.Codex
alias_name=chatgpt-desktop.exe                    ; MSIX 执行别名
exe_path=                                         ; 可选：手动指定真实 ChatGPT.exe
extra_args=                                       ; 追加 Chromium 参数

[clash]
enabled=true                                   ; false = 隐藏 Clash 联动、跳过探测
auto_launch=false                              ; Clash 就绪时自动启动 ChatGPT
pipe_name=                                     ; 可选：手动指定 mihomo 命名管道名（留空自动发现）

[guard]
enabled=true                                   ; 守护模式：发现 ChatGPT 直连时弹提示
auto_takeover=false                            ; 发现直连时自动重启为代理（会中断会话，默认关）

[ui]
refresh_seconds=3
verify_seconds=8
```

### 局域网里的 Clash（代理在另一台机器）

代理跑在局域网另一台电脑 / 软路由上时：

1. 在跑 Clash 的那台机器上打开「允许局域网连接」（mihomo：`allow-lan: true`、`bind-address: "*"`），防火墙放行混合端口 TCP（默认 7897）；路由器若开了「客户端隔离 / AP 隔离」要关掉。
2. 在 ChatGPT 这台机器上把 `config.ini` 的 `[proxy] host` 改成代理机 IP，例如：

   ```ini
   [proxy]
   host=192.168.1.10
   port=7897
   ```

   其余不用改（`no_proxy` 仍保留 127.0.0.1，本机网关如 magpie 的 127.0.0.1:3425 不会被代理）。
3. 先跑 `--selftest`，确认输出里是 `代理: http://192.168.1.10:7897 open=True tunnel=True`，再 `--launch`。

- 状态检测已适配：连接远端等于「代理主机:端口」的会计入「经代理」，不会误报成直连泄漏。
- **Clash 联动（模式 / 节点 / TUN）跨机器用不了**：mihomo 的 REST API 走的是本机命名管道，远端读不到；需要在远端开 HTTP `external-controller` 才能扩展（本工具暂未支持）。
- 代理若开了认证（`authentication`），本工具暂不支持用户名密码，建议内网不开认证，或改用防火墙限制来源 IP。

## 已验证（本项目实测）

测试环境：Windows 11 x64 + ChatGPT 桌面版 `OpenAI.Codex 26.1120.5083.0` + Clash Verge 2.5.10（混合端口 7897）。

- `--selftest`：找到执行别名、代理 `127.0.0.1:7897` 可达、`CONNECT chatgpt.com:443` 返回 `HTTP/1.1 200`、TCP 表读取正常；
- `--launch`：ChatGPT 根进程命令行保留全部代理参数，8 秒内检测到多条 `127.0.0.1:7897` 连接，`codex: 存在=True 走代理=True`，外部远端 0 条；
- `--status`：`ChatGPT: Proxied`、`经代理=15 本地回环=2 外部=0`（本地回环是本地网关的正常流量）；
- `--launch`（已运行时）：返回 3 并提示用 `--restart`；
- `--launch --restart`：关闭后重启成功，状态回到 `Proxied`；
- `--close`：相关进程全部退出；改为「向所有可见窗口发 WM_CLOSE + 2 秒收尾」后，耗时从约 11 秒降到约 3 秒；
- `--clash`：读到 mihomo `v1.19.33`、`mode=rule`、`tun=false`、16 个策略组与 AI 组当前节点；
- `--clash-mode global` → 确认 `mode=global` → `--clash-mode rule` 还原：均成功；
- `--clash-node AI 香港节点` → 确认 `AI → 香港节点` → 还原 `AI → 默认节点`：均成功；
- `--clash-tun on` → 确认 TUN 开启 → `--clash-tun off` → 确认 TUN 关闭：测完 `baidu.com` 直连 200、经代理 `generate_204` 204；
- `--clash-auto on/off`：`config.ini [clash] auto_launch` 持久化正确；
- 托盘冒烟：启动后进程存活、日志无异常、第二个实例不会重复运行。
- 守护模式：直接从执行别名启动 ChatGPT（等价于从开始菜单启动）→ 日志出现「守护模式: 检测到 ChatGPT 未走代理（Unproxied）」，`--status` 显示 `ChatGPT: Unproxied`；
- 自动接管：开启 `--guard-auto on` 并重启托盘 → 30 秒内自动关闭直连实例并以代理方式重启，`--status` 回到 `Proxied`（`经代理=16 外部=0`，codex 走代理）；
- `--diag`：生成约 5.7 KB 的 `diag\diag-*.txt`，包含配置、代理探测、进程、TCP 连接、Clash 状态与日志尾部；
- `--autostart on/off`：注册表 `HKCU\...\Run\ChatGptProxyTray` 写入当前 exe 路径，关闭后删除；
- 第二个实例：静默退出（只写日志），不再弹模态框；
- `test.ps1`：一条命令跑完 8 组共 40+ 项冒烟测试，全部通过。
- `--connections`：列出 `ChatGPT.exe / codex.exe` 到 `127.0.0.1:7897` 的 ESTABLISHED 连接，并从 Clash `/connections` 读到命中 `DomainSuffix/google.com`、链路 `🇭🇰 V香港 10 > 自动选择 > 默认节点` 及上下行字节；
- 状态面板：`--panel` 打开后窗口标题为 `ChatGPT 代理助手 · 状态面板`（可见），`Shown` 后自动拉取连接列表（日志「面板连接已更新: 命中 N 条」），之后每秒刷新；
- 配置热重载：托盘运行中把 `dist\config.ini` 的 `refresh_seconds` 从 3 改成 6、再改回，日志各出现一次「配置已重新加载」，托盘无需重启；
- 托盘双击：弹出状态面板；单击不响应（避免误触，面板不常用时不会误开）；
- 启动菜单项智能切换：ChatGPT 未运行时显示「启动 ChatGPT（走代理）」，检测到直连运行时变成「重启为代理」，已走代理时显示「ChatGPT 已走代理」并禁用；
- 关闭确认：点「关闭 ChatGPT」会先弹确认框（默认焦点在「取消」），避免误关丢失正在生成的内容；
- Clash 模式 / 节点 / TUN 切换后的气泡会注明「仅当前 Clash 会话生效，重启 Clash 后恢复配置」；

## 已知限制

- **只影响本工具启动的实例。** 从开始菜单 / 任务栏直接启动 ChatGPT 仍是直连；托盘检测到「直连」会变黄并提示「重启为代理」。
- **不是 fail-closed 强制。** 没有防火墙/TUN 兜底，代理进程若绕过环境变量仍可能直连（本工具会把它显示为「疑似直连」但不会阻断）。需要绝对强制时，应在 Clash 侧开 TUN + 进程规则，或加防火墙规则。
- MSIX 应用升级后包路径变化不影响本工具（始终走执行别名）；若别名名称变了，改 `config.ini` 的 `alias_name`。
- 仅支持 HTTP 代理（Clash 混合端口 / HTTP 端口）；SOCKS 未接入。
- **Clash 联动依赖 mihomo 的 REST 命名管道**（Clash Verge 默认开启）。其他客户端如果只暴露 HTTP 外部控制器端口，目前连不上，可在 `config.ini` 的 `pipe_name` 手动指定管道名。
- **Clash 联动只支持本机 mihomo 命名管道**：代理在局域网其它机器时，联动菜单显示「API 不可用」，注入与状态检测仍正常。
- **TUN 开关是全局性的**：开启期间整个系统的流量都由 Clash 接管，关掉本工具不会自动还原，需要手动关回去。
- **Clash 的模式 / 节点 / TUN 切换只对当前 Clash 会话生效**（运行时热切换），重启 Clash 后会按其配置文件恢复默认值；要永久生效请直接改 Clash 配置。
- **守护模式的「自动接管」会中断 ChatGPT 当前会话**（未发出的输入可能丢失），默认只提示不自动重启。
- 单实例：托盘只允许运行一个；再次启动会静默退出，只写 `logs\app.log`。

## 故障排查

- 看 `logs\app.log` 和程序目录 `last-result.txt`；
- 启动失败提示找不到执行别名：在 `config.ini` 里把 `target.exe_path` 指向真实的 `ChatGPT.exe`；
- 启动后状态是「直连」：说明单实例机制复用了旧进程，用「重启为代理」；
- 本地网关（如 magpie `127.0.0.1:3425`）报网络错误：确认 `no_proxy` 含 `127.0.0.1`。

## 目录结构

```text
Codex_Proxy/
├─ build.ps1                    # csc 编译脚本
├─ test.ps1                     # 冒烟测试（编译/自检/状态/诊断/配置/自启/托盘/启动关闭）
├─ config.ini                   # 默认配置
├─ LICENSE                      # MIT 许可证
├─ src/
│  ├─ Native.cs                 # P/Invoke（AttachConsole/GetExtendedTcpTable/DestroyIcon…）
│  ├─ AutoStart.cs              # 开机自启（HKCU Run）
│  ├─ Core.cs                   # 日志、配置解析、代理参数/环境变量
│  ├─ Probe.cs                  # Clash 端口探测、CONNECT 验证、TCP 连接表
│  ├─ Clash.cs                  # mihomo REST（命名管道）：模式/节点/TUN/JSON 解析
│  ├─ Diag.cs                   # 诊断报告生成
│  ├─ AppProcess.cs             # WMI 进程查询、状态判定、关闭、定位与启动 ChatGPT
│  ├─ Status.cs                 # 状态聚合（代理/App/连接/泄漏）
│  ├─ Panel.cs                  # 状态面板（状态/连接/规则命中列表）
│  ├─ TrayContext.cs            # 托盘 UI（NotifyIcon + 菜单 + 图标绘制 + 定时刷新）
│  └─ Program.cs                # 入口 + 命令行模式
├─ dist/                        # 编译输出
└─ logs/                        # 运行时日志
```

## 许可证

[MIT License](LICENSE) © 2026 xiaofanying
