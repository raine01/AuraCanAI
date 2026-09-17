<#
  AuraCanAI 发版脚本
  ---------------------------------------------------------------
  做四件事:
    1) 从 csproj 读取 <Version>(必须手动先改好)
    2) Release 构建,产出 bin\Release\AuraCanAI.Dalamud\latest.zip
    3) 同步 pluginmaster.json(AssemblyVersion / 下载链接 / IconUrl / LastUpdate)
    4) 若本机有 gh CLI:自动建 tag 并上传 zip;否则打印手动步骤

  用法:
    powershell -ExecutionPolicy Bypass -File tools\release.ps1
    powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Changelog "修了什么什么"
    powershell -ExecutionPolicy Bypass -File tools\release.ps1 -NoRelease   # 只构建+同步,不发布
#>
[CmdletBinding()]
param(
    [string]$Repo = "raine01/AuraCanAI",
    [string]$Changelog = "",
    [switch]$NoBuild,
    [switch]$NoRelease
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Info($m) { Write-Host "[release] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[release] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "[release] $m" -ForegroundColor Red; exit 1 }

# ---------- 1. 读版本 ----------
$csprojPath = Join-Path $root "AuraCanAI.Dalamud.csproj"
if (-not (Test-Path $csprojPath)) { Fail "找不到 csproj: $csprojPath" }
$csprojText = Get-Content $csprojPath -Raw
$m = [regex]::Match($csprojText, '<Version>\s*([^<\s]+)\s*</Version>')
if (-not $m.Success) { Fail "csproj 里没找到 <Version>" }
$version = $m.Groups[1].Value
Info "版本: $version  仓库: $Repo"

# ---------- 2. 构建 ----------
$zip = Join-Path $root "bin\Release\AuraCanAI.Dalamud\latest.zip"
if ($NoBuild) {
    Warn "跳过构建(-NoBuild)"
} else {
    Info "Release 构建中..."
    & dotnet build -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail "构建失败" }
}
if (-not (Test-Path $zip)) { Fail "找不到打包产物: $zip" }
Info ("产物: {0} ({1:N0} KB)" -f $zip, ((Get-Item $zip).Length / 1KB))

# ---------- 3. 同步 pluginmaster.json ----------
$pmPath = Join-Path $root "pluginmaster.json"
if (-not (Test-Path $pmPath)) { Fail "找不到 pluginmaster.json" }
$pm = Get-Content $pmPath -Raw
$dl  = "https://github.com/$Repo/releases/latest/download/latest.zip"
$ico = "https://raw.githubusercontent.com/$Repo/main/images/icon.png"
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$pm = [regex]::Replace($pm, '"AssemblyVersion"\s*:\s*"[^"]*"',          '"AssemblyVersion": "' + $version + '"')
$pm = [regex]::Replace($pm, '"RepoUrl"\s*:\s*"[^"]*"',                  '"RepoUrl": "https://github.com/' + $Repo + '"')
$pm = [regex]::Replace($pm, '"DownloadLinkInstall"\s*:\s*"[^"]*"',      '"DownloadLinkInstall": "' + $dl + '"')
$pm = [regex]::Replace($pm, '"DownloadLinkUpdate"\s*:\s*"[^"]*"',       '"DownloadLinkUpdate": "' + $dl + '"')
$pm = [regex]::Replace($pm, '"DownloadLinkTesting"\s*:\s*"[^"]*"',      '"DownloadLinkTesting": "' + $dl + '"')
$pm = [regex]::Replace($pm, '"IconUrl"\s*:\s*"[^"]*"',                  '"IconUrl": "' + $ico + '"')
$pm = [regex]::Replace($pm, '"LastUpdate"\s*:\s*\d+',                   '"LastUpdate": ' + $now)

# 校验是合法 JSON
try { $null = $pm | ConvertFrom-Json } catch { Fail "pluginmaster.json 生成了非法 JSON: $_" }
Set-Content -Path $pmPath -Value $pm -Encoding UTF8 -NoNewline
Info "pluginmaster.json 已同步(AssemblyVersion=$version, LastUpdate=$now)"

# ---------- 4. 发布 ----------
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($NoRelease -or -not $gh) {
    if (-not $gh -and -not $NoRelease) { Warn "未检测到 gh CLI,已跳过自动发布" }
    Write-Host ""
    Write-Host "手动发布步骤:" -ForegroundColor Green
    Write-Host "  1. git add -A; git commit -m ""release v$version""; git push"
    Write-Host "  2. 打开 https://github.com/$Repo/releases/new"
    Write-Host "  3. Tag 填 v$version,标题 v$version"
    Write-Host "  4. 把下面这个文件拖进附件区(文件名必须是 latest.zip):"
    Write-Host "     $zip"
    Write-Host "  5. 发布"
    Write-Host ""
    Write-Host "  想自动发布的话装一下:winget install --id GitHub.cli  然后 gh auth login"
    exit 0
}

Info "gh CLI 检测到,创建 Release v$version ..."
$notes = if ($Changelog) { $Changelog } else { "v$version" }
& gh release create "v$version" $zip --repo $Repo --title "v$version" --notes $notes
if ($LASTEXITCODE -ne 0) { Fail "gh release create 失败" }

$url = "https://raw.githubusercontent.com/$Repo/main/pluginmaster.json"
Info "完成。插件源地址(填进卫月自定义插件仓库): $url"
