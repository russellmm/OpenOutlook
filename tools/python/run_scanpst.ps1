<#
run_scanpst.ps1 - drives SCANPST.EXE (Microsoft Outlook Inbox Repair Tool) through Windows UI Automation, ANALYZE ONLY.
It never clicks Repair. For each PST it types the path, clicks Start, waits for the verdict, records the dialog text,
and closes SCANPST. Results are appended to scan_results.txt next to this script.

Usage (PowerShell, from F:\Claude; do not touch the mouse/keyboard while it runs - it needs the desktop):
    powershell -ExecutionPolicy Bypass -File .\run_scanpst.ps1 test_W6.pst
    powershell -ExecutionPolicy Bypass -File .\run_scanpst.ps1 test_W0.pst test_W3.pst test_W6.pst
Close Outlook first. SCANPST must be on PATH (or pass -ScanPst "C:\Program Files\Microsoft Office\root\Office16\SCANPST.EXE").
Verdict values: NO_ERRORS, MINOR, ERRORS, TIMEOUT, ERROR(...).
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Files,
    [string]$ScanPst = 'scanpst.exe',
    [string]$Out = '',
    [int]$TimeoutSec = 900,
    [switch]$Dump
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $here) { $here = (Get-Location).Path }
if (-not $Out) { $Out = Join-Path $here 'scan_results.txt' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Get-Windows([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    $AE::RootElement.FindAll($TS::Children, $cond)
}
function Get-All($root, $type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
    $root.FindAll($TS::Descendants, $cond)
}
function Get-Texts([int]$procId) {
    $t = @()
    foreach ($w in (Get-Windows $procId)) {
        foreach ($e in (Get-All $w $CT::Text)) { if ($e.Current.Name) { $t += $e.Current.Name } }
    }
    $t
}
function Find-Button([int]$procId, [string]$pattern) {
    foreach ($w in (Get-Windows $procId)) {
        foreach ($b in (Get-All $w $CT::Button)) {
            if ($b.Current.Name -replace '&', '' -match $pattern) { return $b }
        }
    }
    $null
}
function Click($btn) {
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Dump-Tree($win, [string]$path) {
    $cond = [System.Windows.Automation.Condition]::TrueCondition
    $lines = @()
    foreach ($e in $win.FindAll($TS::Descendants, $cond)) {
        $c = $e.Current
        $pats = @($e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'PatternIdentifiers.Pattern', '' }) -join ','
        $lines += ('{0,-14} name="{1}" class={2} id={3} patterns={4}' -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $c.ClassName, $c.AutomationId, $pats)
    }
    Set-Content -LiteralPath $path -Value $lines
}
function Find-PathBox($win) {
    # any element that supports ValuePattern and is editable, preferring Edit / ComboBox
    $cond = [System.Windows.Automation.Condition]::TrueCondition
    $best = $null
    foreach ($e in $win.FindAll($TS::Descendants, $cond)) {
        $t = $e.Current.ControlType
        if ($t -ne $CT::Edit -and $t -ne $CT::ComboBox) { continue }
        $p = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) {
            if (-not $p.Current.IsReadOnly) { return @{ Element = $e; Pattern = $p } }
        }
    }
    $null
}

function Scan-One([string]$file) {
    $full = (Resolve-Path -LiteralPath $file).Path
    $cmd = Get-Command $ScanPst -ErrorAction Stop
    $proc = Start-Process -FilePath $cmd.Source -PassThru
    try {
        # wait for the main window
        $deadline = (Get-Date).AddSeconds(30)
        $win = $null
        while ((Get-Date) -lt $deadline -and -not $win) {
            Start-Sleep -Milliseconds 500
            $proc.Refresh()
            if ($proc.HasExited) { throw 'SCANPST exited at start-up' }
            $ws = Get-Windows $proc.Id
            if ($ws.Count -gt 0) { $win = $ws[0] }
        }
        if (-not $win) { throw 'SCANPST window did not appear' }
        Start-Sleep -Milliseconds 800
        # path box
        if ($Dump) {
            $dumpPath = Join-Path $here 'scanpst_tree.txt'
            Dump-Tree $win $dumpPath
            return [pscustomobject]@{ File = $file; Verdict = 'DUMP'; Folders = ''; Items = ''; Text = "tree written to $dumpPath" }
        }
        $pb = Find-PathBox $win
        if ($pb) {
            $pb.Pattern.SetValue($full)
        } else {
            # fallback: type into the focused field with the keyboard
            Add-Type -AssemblyName System.Windows.Forms
            try { $win.SetFocus() } catch {}
            Start-Sleep -Milliseconds 400
            [System.Windows.Forms.Clipboard]::SetText($full)
            [System.Windows.Forms.SendKeys]::SendWait('^a')
            [System.Windows.Forms.SendKeys]::SendWait('^v')
        }
        Start-Sleep -Milliseconds 400
        $start = Find-Button $proc.Id '^Start$'
        if (-not $start) { throw 'Start button not found' }
        Click $start
        # wait for a verdict
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        $verdict = $null; $texts = @()
        while ((Get-Date) -lt $deadline -and -not $verdict) {
            Start-Sleep -Seconds 2
            if ($proc.HasExited) { throw 'SCANPST exited during the scan' }
            $texts = @(Get-Texts $proc.Id)
            $joined = $texts -join ' | '
            if ($joined -match 'No errors were found') { $verdict = 'NO_ERRORS' }
            elseif ($joined -match 'Only minor inconsistencies') { $verdict = 'MINOR' }
            elseif ($joined -match '(?i)errors were found|Errors were found') { $verdict = 'ERRORS' }
        }
        if (-not $verdict) { $verdict = 'TIMEOUT' }
        $folders = ''; $items = ''
        foreach ($t in $texts) {
            if ($t -match 'Folders found in this file:\s*(\d+)') { $folders = $Matches[1] }
            if ($t -match 'Items found in this file:\s*(\d+)') { $items = $Matches[1] }
            if ($t -match 'Folders found' -and $t -match 'Items found') {
                if ($t -match 'Folders found in this file:\s*(\d+)') { $folders = $Matches[1] }
                if ($t -match 'Items found in this file:\s*(\d+)') { $items = $Matches[1] }
            }
        }
        # dismiss WITHOUT repairing: prefer OK, else Cancel (never Repair)
        $b = Find-Button $proc.Id '^OK$'
        if (-not $b) { $b = Find-Button $proc.Id '^Cancel$' }
        if ($b) { Click $b; Start-Sleep -Milliseconds 800 }
        [pscustomobject]@{ File = $file; Verdict = $verdict; Folders = $folders; Items = $items; Text = ($texts -join ' | ') }
    }
    finally {
        if (-not $proc.HasExited) {
            [void]$proc.CloseMainWindow()
            Start-Sleep -Seconds 2
            if (-not $proc.HasExited) { $proc.Kill() }
        }
    }
}

if (-not $Files -or $Files.Count -eq 0) { Write-Host 'Give one or more .pst file names.'; exit 2 }
foreach ($f in $Files) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    try {
        $r = Scan-One $f
        $logName = [IO.Path]::ChangeExtension((Resolve-Path -LiteralPath $f).Path, '.log')
        $bang = ''
        if (Test-Path -LiteralPath $logName) {
            $lines = Get-Content -LiteralPath $logName -ErrorAction SilentlyContinue
            $bang = ' log:{0} lines, {1} flagged (!!/??)' -f $lines.Count, @($lines | Where-Object { $_ -match '!!|\?\?' }).Count
        }
        $line = '{0}  {1,-16} {2,-9} folders={3} items={4}{5}' -f $stamp, $r.File, $r.Verdict, $r.Folders, $r.Items, $bang
    }
    catch {
        $line = '{0}  {1,-16} ERROR({2})' -f $stamp, $f, $_.Exception.Message
    }
    Write-Host $line
    Add-Content -LiteralPath $Out -Value $line
}
Write-Host "Done. Results are in $Out"
