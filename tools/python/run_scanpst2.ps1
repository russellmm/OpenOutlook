<#
run_scanpst2.ps1 - ANALYZE-ONLY driver for SCANPST.EXE using plain Win32 messages (the Office 16 SCANPST exposes its controls as
unpatterned panes, which run_scanpst.ps1's UI Automation lookup cannot use). Sets the file name, presses Start, waits for the verdict dialog,
records its text and the Folders/Items counts, presses OK / closes. It never presses Repair.
Usage: powershell -ExecutionPolicy Bypass -File .\run_scanpst2.ps1 test_C1.pst [more.pst ...]
Results are appended to scan_results.txt; SCANPST also writes <file>.log next to the PST.
#>
[CmdletBinding(PositionalBinding = $false)]
param([Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Files, [int]$TimeoutSec = 900, [switch]$Repair)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  public static List<IntPtr> TopWindows(uint pid) {
    var r = new List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, IntPtr.Zero);
    return r;
  }
  public static List<string[]> Children(IntPtr top) {   // {class, text, hwnd}
    var r = new List<string[]>();
    EnumChildWindows(top, (h, l) => {
      var c = new StringBuilder(64); var t = new StringBuilder(4096);
      GetClassName(h, c, 64); GetWindowText(h, t, 4096);
      r.Add(new string[] { c.ToString(), t.ToString(), h.ToInt64().ToString() });
      return true; }, IntPtr.Zero);
    return r;
  }
  public static string Title(IntPtr h) { var t = new StringBuilder(512); GetWindowText(h, t, 512); return t.ToString(); }
}
'@
$here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
function Texts($pid_) { foreach ($w in [W]::TopWindows($pid_)) { [W]::Title($w); foreach ($c in [W]::Children($w)) { if ($c[1]) { $c[1] } } } }
function ClickButton($pid_, $pattern) {
    foreach ($w in [W]::TopWindows($pid_)) {
        foreach ($c in [W]::Children($w)) {
            if ($c[0] -eq 'Button' -and (($c[1] -replace '&', '') -match $pattern)) { [void][W]::SendMessage([IntPtr][int64]$c[2], 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero); return $true }   # BM_CLICK
        }
    }
    $false
}
foreach ($f in $Files) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $full = (Resolve-Path -LiteralPath $f).Path
    $proc = Start-Process -FilePath 'scanpst.exe' -PassThru
    try {
        $main = [IntPtr]::Zero
        for ($i = 0; $i -lt 60 -and $main -eq [IntPtr]::Zero; $i++) {
            Start-Sleep -Milliseconds 500
            foreach ($w in [W]::TopWindows($proc.Id)) { if ([W]::GetDlgItem($w, 1004) -ne [IntPtr]::Zero) { $main = $w } }
        }
        if ($main -eq [IntPtr]::Zero) { throw 'SCANPST window did not appear' }
        [void][W]::SendMessage([W]::GetDlgItem($main, 1004), 0x000C, [IntPtr]::Zero, $full)     # WM_SETTEXT
        Start-Sleep -Milliseconds 500
        if (-not (ClickButton $proc.Id '^Start$')) { throw 'Start button not found' }
        $deadline = (Get-Date).AddSeconds($TimeoutSec); $verdict = $null; $texts = @()
        while ((Get-Date) -lt $deadline -and -not $verdict) {
            Start-Sleep -Seconds 2
            if ($proc.HasExited) { throw 'SCANPST exited during the scan' }
            $texts = @(Texts $proc.Id)
            $j = $texts -join ' | '
            if ($j -match 'No errors were found') { $verdict = 'NO_ERRORS' }
            elseif ($j -match 'minor inconsistencies') { $verdict = 'MINOR' }
            elseif ($j -match '(?i)errors were found|Errors were found') { $verdict = 'ERRORS' }
        }
        if (-not $verdict) { $verdict = 'TIMEOUT' }
        $folders = ''; $items = ''
        foreach ($t in $texts) {
            if ($t -match 'Folders found in this file:\s*(\d+)') { $folders = $Matches[1] }
            if ($t -match 'Items found in this file:\s*(\d+)') { $items = $Matches[1] }
        }
        if ($Repair -and $verdict -ne 'NO_ERRORS' -and $verdict -ne 'TIMEOUT') {      # only ever use -Repair on a throwaway COPY
            # untick "Make backup of scanned file before repairing" (BM_SETCHECK = 0xF1, BST_UNCHECKED = 0): no .bak, so no overwrite prompt on later passes
            foreach ($w in [W]::TopWindows($proc.Id)) {
                foreach ($c in [W]::Children($w)) {
                    if ($c[0] -eq 'Button' -and (($c[1] -replace '&', '') -match 'Make backup')) { [void][W]::SendMessage([IntPtr][int64]$c[2], 0x00F1, [IntPtr]::Zero, [IntPtr]::Zero) }
                }
            }
            Start-Sleep -Milliseconds 400
            [void](ClickButton $proc.Id '^Repair$')
            $rd = (Get-Date).AddSeconds($TimeoutSec); $done = $false
            while ((Get-Date) -lt $rd -and -not $done) {
                Start-Sleep -Seconds 2
                if ($proc.HasExited) { break }
                $t2 = @(Texts $proc.Id)
                if (($t2 -join ' | ') -match '(?i)repair complete') { $done = $true; $texts = $t2 }
            }
            $verdict = $verdict + '+REPAIRED'
        }
        [void](ClickButton $proc.Id '^OK$')            # dismiss the dialog; without -Repair, Repair is never pressed
        Start-Sleep -Milliseconds 800
        $log = [IO.Path]::ChangeExtension($full, '.log'); $bang = ''
        if (Test-Path -LiteralPath $log) { $l = Get-Content -LiteralPath $log; $bang = ' log:{0} lines, {1} flagged (!!/??)' -f $l.Count, @($l | ? { $_ -match '!!|\?\?' }).Count }
        $line = '{0}  {1,-16} {2,-9} folders={3} items={4}{5}' -f $stamp, $f, $verdict, $folders, $items, $bang
        $line += "`n    dialog: " + (($texts | ? { $_ -and $_ -notmatch '^(Microsoft Outlook Inbox Repair Tool|Start|Close|Options\.\.\.|Browse\.\.\.)$' }) -join ' | ')
    }
    catch { $line = '{0}  {1,-16} ERROR({2})' -f $stamp, $f, $_.Exception.Message }
    finally { if (-not $proc.HasExited) { [void]$proc.CloseMainWindow(); Start-Sleep 2; if (-not $proc.HasExited) { $proc.Kill() } } }
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $here 'scan_results.txt') -Value $line
}
