[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$ResetConfig
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDir = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $compiler)) {
    throw "找不到 csc.exe（需要 .NET Framework 4.x）: $compiler"
}

if ($Clean -and (Test-Path $distDir)) {
    Remove-Item $distDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$sources = @(Get-ChildItem -Path $sourceDir -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) {
    throw "src 目录下没有 .cs 文件: $sourceDir"
}

$output = Join-Path $distDir 'ChatGptProxyTray.exe'
$iconPath = Join-Path $root 'app.ico'
$manifestPath = Join-Path $sourceDir 'app.manifest'
$compilerArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/utf8output'
    '/optimize+'
    ('/out:' + $output)
)
if (Test-Path $iconPath) {
    $compilerArgs += ('/win32icon:' + $iconPath)
}
if (Test-Path $manifestPath) {
    $compilerArgs += ('/win32manifest:' + $manifestPath)
}
$compilerArgs += $sources

& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "编译失败，csc 退出码 $LASTEXITCODE"
}

$configSource = Join-Path $root 'config.ini'
$configDest = Join-Path $distDir 'config.ini'
if ((Test-Path $configSource) -and ($ResetConfig -or -not (Test-Path $configDest))) {
    Copy-Item $configSource $configDest -Force
}

Write-Host ("编译成功: " + $output)
