# AuraCanAI 合奏诊断(提权版):一次 UAC 拿到全部关键信息
# 1. 两个游戏进程的完整性级别(确认 UIPI 假设)
# 2. 辅端(第二个客户端)的启动方式和父进程链(双开方式)
# 3. 管理员权限下 PostMessage 注入辅端窗口是否成功(验证"主端以管理员运行"方案可行性)
# 结果输出到 D:\AuraCanAI.Dalamud\tools\diag-out.txt

$out = "D:\AuraCanAI.Dalamud\tools\diag-out.txt"
"=== AuraCanAI 提权诊断 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Out-File $out -Encoding UTF8

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class Diag {
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr t);
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool GetTokenInformation(IntPtr t, int cls, IntPtr info, int len, out int ret);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint acc, bool inherit, int pid);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr hWnd, uint msg, uint wParam, uint lParam);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
}
'@

$map = @{ 'S-1-16-4096'='Untrusted'; 'S-1-16-8192'='Low(低)'; 'S-1-16-12288'='Medium(中)'; 'S-1-16-16384'='High(高)'; 'S-1-16-20480'='System' }

function Get-IntegrityLabel([int]$pid) {
  $ph = [Diag]::OpenProcess(0x1000, $false, $pid) # QUERY_LIMITED
  if ($ph -eq [IntPtr]::Zero) { return "无法打开(err=$([Runtime.InteropServices.Marshal]::GetLastWin32Error()))" }
  $tok = [IntPtr]::Zero
  if (-not [Diag]::OpenProcessToken($ph, 0x0008, [ref]$tok)) { [Diag]::CloseHandle($ph) | Out-Null; return "OpenToken失败" }
  $size = 0
  [Diag]::GetTokenInformation($tok, 25, [IntPtr]::Zero, 0, [ref]$size) | Out-Null
  $buf = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
  [Diag]::GetTokenInformation($tok, 25, $buf, $size, [ref]$size) | Out-Null
  $sidPtr = [Runtime.InteropServices.Marshal]::ReadIntPtr($buf)
  $sid = (New-Object Security.Principal.SecurityIdentifier($sidPtr)).ToString()
  $label = if ($map.ContainsKey($sid)) { $map[$sid] } else { $sid }
  [Runtime.InteropServices.Marshal]::FreeHGlobal($buf)
  [Diag]::CloseHandle($tok) | Out-Null
  [Diag]::CloseHandle($ph) | Out-Null
  return $label
}

# --- 1. 游戏进程完整性 ---
"`n[1] 游戏进程完整性:" | Out-File $out -Append -Encoding UTF8
$games = @(Get-Process ffxiv_dx11 -ErrorAction SilentlyContinue)
if ($games.Count -eq 0) { "未找到 ffxiv_dx11 进程!" | Out-File $out -Append -Encoding UTF8 }
foreach ($g in $games) {
  $il = Get-IntegrityLabel $g.Id
  ("  PID={0} HWND=0x{1:X} 完整性={2} 标题='{3}'" -f $g.Id, $g.MainWindowHandle, $il, $g.MainWindowTitle) | Out-File $out -Append -Encoding UTF8
}

# --- 2. 进程启动链 ---
"`n[2] 启动方式(辅端/主端命令行+父进程):" | Out-File $out -Append -Encoding UTF8
foreach ($g in $games) {
  $p = Get-CimInstance Win32_Process -Filter "ProcessId=$($g.Id)"
  if ($p) {
    ("  PID=$($g.Id) 命令行: $($p.CommandLine)") | Out-File $out -Append -Encoding UTF8
    $pp = $p.ParentProcessId
    $gp = Get-CimInstance Win32_Process -Filter "ProcessId=$pp" -ErrorAction SilentlyContinue
    if ($gp) {
      ("    父进程 PID=$pp 名称=$($gp.Name) 命令行: $($gp.CommandLine)") | Out-File $out -Append -Encoding UTF8
      $il2 = Get-IntegrityLabel $pp
      ("    父进程完整性=$il2") | Out-File $out -Append -Encoding UTF8
    } else { ("    父进程 PID=$pp 已退出") | Out-File $out -Append -Encoding UTF8 }
  }
}

# --- 3. 管理员权限注入测试(向所有游戏窗口注入 Q 键音高60) ---
"`n[3] 管理员权限 PostMessage 注入测试(向每个游戏窗口注入 音高60=Q 键):" | Out-File $out -Append -Encoding UTF8
foreach ($g in $games) {
  $hwnd = $g.MainWindowHandle
  if ($hwnd -eq [IntPtr]::Zero) { ("  PID=$($g.Id) MainWindowHandle=0,跳过") | Out-File $out -Append -Encoding UTF8; continue }
  $r1 = [Diag]::PostMessage($hwnd, 0x0100, 0x51, 0)
  $e1 = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
  Start-Sleep -Milliseconds 500
  $r2 = [Diag]::PostMessage($hwnd, 0x0101, 0x51, 0)
  ("  PID=$($g.Id) HWND=0x{0:X} KeyDown返回={1}(err={2}) KeyUp返回={3}" -f $hwnd.ToInt64(), $r1, $e1, $r2) | Out-File $out -Append -Encoding UTF8
}

"`n=== 完成 ===" | Out-File $out -Append -Encoding UTF8
Write-Host "诊断完成,结果已写入 $out" -ForegroundColor Green
Start-Process notepad $out
