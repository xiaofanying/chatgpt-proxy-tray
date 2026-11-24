# ChatGPT 代理助手 —— 冒烟测试
# 用法（在仓库根目录）：
#   pwsh -NoProfile -ExecutionPolicy Bypass -File test.ps1
#   pwsh -NoProfile -ExecutionPolicy Bypass -File test.ps1 -SkipLaunch     # 不启动/关闭 ChatGPT
#   pwsh -NoProfile -ExecutionPolicy Bypass -File test.ps1 -SkipAutostart  # 不动注册表
param(
    [switch]$SkipLaunch,
    [switch]$SkipAutostart
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'dist\ChatGptProxyTray.exe'
$distConfig = Join-Path $root 'dist\config.ini'
$lastResult = Join-Path $root 'dist\last-result.txt'
$logFile = Join-Path $root 'dist\logs\app.log'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'ChatGptProxyTray'
$script:passed = 0
$script:failed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        Write-Host ("  [PASS] " + $Name) -ForegroundColor Green
        $script:passed++
    }
    else {
        $suffix = ''
        if ($Detail) { $suffix = ' — ' + $Detail }
        Write-Host ("  [FAIL] " + $Name + $suffix) -ForegroundColor Red
        $script:failed++
    }
}

function Invoke-Cli {
    param([string[]]$Arguments)
    if (Test-Path $lastResult) { Remove-Item $lastResult -Force }
    $process = Start-Process -FilePath $exe -ArgumentList $Arguments -Wait -PassThru
    $text = ''
    if (Test-Path $lastResult) { $text = Get-Content $lastResult -Encoding UTF8 -Raw }
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Text = $text }
}

function Get-IniValue {
    param([string]$Path, [string]$Section, [string]$Key)
    $current = ''
    foreach ($line in (Get-Content $Path -Encoding UTF8)) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[(.+)\]$') { $current = $Matches[1]; continue }
        if ($current -ne $Section) { continue }
        $eq = $trimmed.IndexOf('=')
        if ($eq -le 0) { continue }
        if ($trimmed.Substring(0, $eq).Trim() -eq $Key) { return $trimmed.Substring($eq + 1).Trim() }
    }
    return $null
}

Write-Host ''
Write-Host '== 1/8 编译 ==' -ForegroundColor Cyan
& (Join-Path $root 'build.ps1') | Out-Host
Assert-True '生成 dist\ChatGptProxyTray.exe' (Test-Path $exe)
Assert-True 'dist\config.ini 含 [guard] 段' ((Get-Content $distConfig -Encoding UTF8 -Raw) -match '\[guard\]')

$r = Invoke-Cli @('-h')
Assert-True '-h 帮助 exit=0' ($r.ExitCode -eq 0 -and $r.Text -match '用法:') ('exit=' + $r.ExitCode)
$r = Invoke-Cli @('/?')
Assert-True '/? 帮助 exit=0' ($r.ExitCode -eq 0 -and $r.Text -match '用法:') ('exit=' + $r.ExitCode)
$r = Invoke-Cli @('--clash-tun', 'invalid')
Assert-True '--clash-tun 非法参数返回 2' ($r.ExitCode -eq 2) ('exit=' + $r.ExitCode)
$r = Invoke-Cli @('--clash-auto', 'invalid')
Assert-True '--clash-auto 非法参数返回 2' ($r.ExitCode -eq 2) ('exit=' + $r.ExitCode)

Write-Host ''
Write-Host '== 2/8 自检 ==' -ForegroundColor Cyan
$r = Invoke-Cli @('--selftest')
Assert-True '--selftest exit=0' ($r.ExitCode -eq 0) ('exit=' + $r.ExitCode)
Assert-True '自检结果通过' ($r.Text -match '结果: 通过')
Assert-True '报告含守护/开机自启信息' (($r.Text -match '守护模式') -and ($r.Text -match '开机自启'))

Write-Host ''
Write-Host '== 3/8 状态与诊断报告 ==' -ForegroundColor Cyan
$r = Invoke-Cli @('--status')
Assert-True '--status exit=0' ($r.ExitCode -eq 0) ('exit=' + $r.ExitCode)
Assert-True '状态报告含 ChatGPT 行' ($r.Text -match 'ChatGPT:')

$r = Invoke-Cli @('--connections')
Assert-True '--connections exit=0' ($r.ExitCode -eq 0) ('exit=' + $r.ExitCode)
Assert-True '连接报告含进程连接与命中段' (($r.Text -match '进程 ESTABLISHED 连接') -and ($r.Text -match 'Clash /connections 命中'))

$r = Invoke-Cli @('--diag')
Assert-True '--diag exit=0' ($r.ExitCode -eq 0) ('exit=' + $r.ExitCode)
$diagFile = $null
if ($r.Text -match '诊断报告已生成: (.+\.txt)') { $diagFile = $Matches[1].Trim() }
Assert-True '诊断文件存在' (($null -ne $diagFile) -and (Test-Path $diagFile)) $diagFile
if ($diagFile -and (Test-Path $diagFile)) {
    $diagText = Get-Content $diagFile -Encoding UTF8 -Raw
    Assert-True '诊断含配置/状态/日志段' (($diagText -match '== 配置 ==') -and ($diagText -match '== 代理与状态 ==') -and ($diagText -match '== 日志尾部'))
}

Write-Host ''
Write-Host '== 4/8 守护模式配置 ==' -ForegroundColor Cyan
$r = Invoke-Cli @('--guard', 'on')
Assert-True '--guard on exit=0' ($r.ExitCode -eq 0)
Assert-True 'guard.enabled=true 已写入' ((Get-IniValue $distConfig 'guard' 'enabled') -eq 'true')
$r = Invoke-Cli @('--guard', 'off')
Assert-True '--guard off exit=0' ($r.ExitCode -eq 0)
Assert-True 'guard.enabled=false 已写入' ((Get-IniValue $distConfig 'guard' 'enabled') -eq 'false')
$r = Invoke-Cli @('--guard', 'on')
Assert-True '守护模式已还原为开启' ((Get-IniValue $distConfig 'guard' 'enabled') -eq 'true')

$r = Invoke-Cli @('--guard-auto', 'on')
Assert-True '--guard-auto on exit=0' ($r.ExitCode -eq 0)
Assert-True 'guard.auto_takeover=true 已写入' ((Get-IniValue $distConfig 'guard' 'auto_takeover') -eq 'true')
$r = Invoke-Cli @('--guard-auto', 'off')
Assert-True '--guard-auto off exit=0' ($r.ExitCode -eq 0)
Assert-True 'guard.auto_takeover=false 已写入' ((Get-IniValue $distConfig 'guard' 'auto_takeover') -eq 'false')
$guardSectionCount = ([regex]::Matches((Get-Content $distConfig -Encoding UTF8 -Raw), '(?m)^\[guard\]\s*$')).Count
Assert-True 'config.ini 未出现重复 [guard] 节' ($guardSectionCount -eq 1) ('count=' + $guardSectionCount)

Write-Host ''
Write-Host '== 5/8 开机自启（HKCU Run） ==' -ForegroundColor Cyan
if ($SkipAutostart) {
    Write-Host '  [SKIP] 已指定 -SkipAutostart' -ForegroundColor DarkYellow
}
else {
    $prior = $null
    $priorItem = Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
    if ($null -ne $priorItem) { $prior = $priorItem.$runValue }

    $r = Invoke-Cli @('--autostart', 'on')
    Assert-True '--autostart on exit=0' ($r.ExitCode -eq 0)
    $value = (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue
    Assert-True '注册表写入当前程序路径' ($value -eq ('"' + $exe + '"')) $value

    $r = Invoke-Cli @('--autostart', 'off')
    Assert-True '--autostart off exit=0' ($r.ExitCode -eq 0)
    $value = (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue
    Assert-True '注册表值已删除' ($null -eq $value)

    if ($prior) {
        Set-ItemProperty -Path $runKey -Name $runValue -Value $prior
        Write-Host '  （已还原测试前的开机自启项）' -ForegroundColor DarkYellow
    }
}

Write-Host ''
Write-Host '== 6/8 托盘与单实例 ==' -ForegroundColor Cyan
Get-Process ChatGptProxyTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$tray = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5
Assert-True '托盘进程存活' (-not $tray.HasExited)
$logText = ''
if (Test-Path $logFile) { $logText = Get-Content $logFile -Encoding UTF8 -Raw }
Assert-True '日志记录托盘已启动' ($logText -match '托盘已启动')
$second = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3
Assert-True '第二个实例静默退出（无模态框）' ($second.HasExited)
$wake = Invoke-Cli @('--panel')
Assert-True '托盘运行时 --panel 唤醒已有面板并返回 0' ($wake.ExitCode -eq 0 -and $wake.Text -match '已唤醒') ('exit=' + $wake.ExitCode + ' text=' + $wake.Text)
Get-Process ChatGptProxyTray -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host ''
Write-Host '== 7/8 启动 / 代理生效 / 关闭 ==' -ForegroundColor Cyan
if ($SkipLaunch) {
    Write-Host '  [SKIP] 已指定 -SkipLaunch' -ForegroundColor DarkYellow
}
else {
    $r = Invoke-Cli @('--launch')
    if ($r.ExitCode -eq 3) {
        Write-Host '  ChatGPT 已在运行，改用 --launch --restart' -ForegroundColor DarkYellow
        $r = Invoke-Cli @('--launch', '--restart')
    }
    Assert-True '--launch exit=0' ($r.ExitCode -eq 0) ('exit=' + $r.ExitCode)
    Assert-True '启动后状态为 Proxied' ($r.Text -match 'ChatGPT: Proxied')
    Assert-True '无外部直连（疑似直连: 无）' ($r.Text -match '疑似直连: 无')

    $r = Invoke-Cli @('--connections')
    Assert-True '--connections 有经代理连接' ($r.Text -match '\[经代理\]') $r.Text

    $r = Invoke-Cli @('--status')
    Assert-True '--status 显示 Proxied' ($r.Text -match 'ChatGPT: Proxied')

    $r = Invoke-Cli @('--close')
    Assert-True '--close exit=0' ($r.ExitCode -eq 0)
    $r = Invoke-Cli @('--status')
    Assert-True '关闭后状态为 NotRunning' ($r.Text -match 'ChatGPT: NotRunning')
}

Write-Host ''
Write-Host '== 8/8 状态面板与配置热重载 ==' -ForegroundColor Cyan
Get-Process ChatGptProxyTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$lineBefore = 0
if (Test-Path $logFile) { $lineBefore = (Get-Content $logFile).Count }
$panel = Start-Process -FilePath $exe -ArgumentList @('--panel') -PassThru
Start-Sleep -Seconds 5
Assert-True '状态面板进程存活' (-not $panel.HasExited)
$newLog = ''
if (Test-Path $logFile) { $newLog = ((Get-Content $logFile | Select-Object -Skip $lineBefore) -join "`n") }
Assert-True '日志记录状态面板已打开' ($newLog -match '状态面板已打开')
Assert-True '面板已拉取连接列表' ($newLog -match '面板连接已更新')
Stop-Process -Id $panel.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Get-Process ChatGptProxyTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$original = Get-Content $distConfig -Encoding UTF8 -Raw
$lineBefore = 0
if (Test-Path $logFile) { $lineBefore = (Get-Content $logFile).Count }
$tray = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($distConfig, $original.Replace('refresh_seconds=3', 'refresh_seconds=6'), $utf8)
Start-Sleep -Seconds 6
$newLog = ''
if (Test-Path $logFile) { $newLog = ((Get-Content $logFile | Select-Object -Skip $lineBefore) -join "`n") }
Assert-True '修改 config.ini 后自动热重载' ($newLog -match '配置已重新加载')
[System.IO.File]::WriteAllText($distConfig, $original, $utf8)
Start-Sleep -Seconds 3
Get-Process ChatGptProxyTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host ''
Write-Host ('== 结果: 通过 ' + $script:passed + ' 项，失败 ' + $script:failed + ' 项 ==') -ForegroundColor Cyan
if ($script:failed -gt 0) { exit 1 }
exit 0
