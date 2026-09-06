# AuraCanAI 合奏诊断脚本:验证 PostMessage 注入到辅端(第二个客户端)窗口是否有效
# 用法:
#   1. 双开 FF14,辅端角色持乐器并进入演奏模式(演奏界面打开)
#   2. powershell -ExecutionPolicy Bypass -File test-slave-inject.ps1            # 列出窗口
#   3. powershell -ExecutionPolicy Bypass -File test-slave-inject.ps1 -Pid 13360 # 向指定辅端注入测试键(音高60=Q)
# 注入后辅端应弹出一个音(Q 键音高 60)。若没有任何反应 → PostMessage 到辅端窗口无效。

param(
    [int]$TargetPid = 0, # 目标辅端进程 PID;0 = 只列窗口
    [int]$Pitch = 60     # 注入的音符(48~84),默认 60(C4=Q 键)
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Test {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, uint wParam, uint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
}
'@

$keymap = @{
    48=0x49; 49=0x38; 50=0x4F; 51=0x39; 52=0x50; 53=0xDB; 54=0x30; 55=0xDD; 56=0xBD; 57=0xDC;
    58=0xBB; 59=0xDE; 60=0x51; 61=0x32; 62=0x57; 63=0x33; 64=0x45; 65=0x52; 66=0x35; 67=0x54;
    68=0x36; 69=0x59; 70=0x37; 71=0x55; 72=0x5A; 73=0x53; 74=0x58; 75=0x44; 76=0x43; 77=0x56;
    78=0x47; 79=0x42; 80=0x48; 81=0x4E; 82=0x4A; 83=0x4D; 84=0xBF
}

$procs = @(Get-Process ffxiv_dx11 -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Write-Host "未找到 ffxiv_dx11 进程(游戏没开?)" -ForegroundColor Red; exit 1 }
Write-Host ("发现 {0} 个 FF14 客户端:" -f $procs.Count)

$win = @{}
foreach ($p in $procs) {
    $h = $p.MainWindowHandle
    $sb = New-Object System.Text.StringBuilder 256
    [Win32Test]::GetWindowText($h, $sb, 256) | Out-Null
    $vis  = if ($h -ne 0) { [Win32Test]::IsWindowVisible($h) } else { $false }
    $icon = if ($h -ne 0) { [Win32Test]::IsIconic($h) } else { $false }
    $en   = if ($h -ne 0) { [Win32Test]::IsWindowEnabled($h) } else { $false }
    $ok   = if ($h -ne 0) { [Win32Test]::IsWindow($h) } else { $false }
    Write-Host ("  PID={0,-6} HWND=0x{1:X} 有效={2} 可见={3} 最小化={4} 启用={5} 标题='{6}'" -f $p.Id, $h, $ok, $vis, $icon, $en, $sb.ToString())
    $win[$p.Id] = $h
}

if ($TargetPid -eq 0) { Write-Host "`n指定 -TargetPid <辅端PID> 注入测试键(仅改 -TargetPid 参数重跑)。当前进程 PID=$([System.Diagnostics.Process]::GetCurrentProcess().Id)"; exit 0 }

if (-not $win.ContainsKey($TargetPid)) { Write-Host "PID $TargetPid 不是 ffxiv_dx11 进程!" -ForegroundColor Red; exit 1 }
$hwnd = $win[$TargetPid]
if (-not $keymap.ContainsKey($Pitch)) { Write-Host "Pitch $Pitch 不在 48~84 范围!" -ForegroundColor Red; exit 1 }

$vk = $keymap[$Pitch]
Write-Host ("注入: HWND=0x{0:X} 音高={1} VK=0x{2:X} WM_KEYDOWN -> 500ms -> WM_KEYUP" -f $hwnd, $Pitch, $vk) -ForegroundColor Yellow
Write-Host "请确认辅端角色已持乐器并进入演奏模式(演奏界面打开),然后看辅端是否弹出这个音(Q 键音高)。"
$r1 = [Win32Test]::PostMessage($hwnd, 0x0100, $vk, 0)   # WM_KEYDOWN
Write-Host ("WM_KEYDOWN PostMessage 返回: {0}" -f $r1) -ForegroundColor Cyan
Start-Sleep -Milliseconds 500
$r2 = [Win32Test]::PostMessage($hwnd, 0x0101, $vk, 0)   # WM_KEYUP
Write-Host ("WM_KEYUP PostMessage 返回: {0}" -f $r2) -ForegroundColor Cyan
Write-Host "已发送。" -ForegroundColor Green
