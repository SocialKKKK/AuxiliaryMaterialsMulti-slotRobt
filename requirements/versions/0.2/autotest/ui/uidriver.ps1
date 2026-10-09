# uidriver.ps1 - step-driven UI Automation driver for the WireCabinet HMI (WPF).
#
# Why this exists: the on-site acceptance checklist is a GUI procedure, but the HMI is
# only reachable from the machine it runs on. This script runs ON that machine (started
# through a scheduled task, so it sits in the interactive session of the logged-on user),
# replays a JSON step list, writes back one JSON result file plus PNG screenshots.
# The caller then pulls those artifacts over SSH/scp and asserts against app.db.
#
# ASCII-ONLY ON PURPOSE. Windows PowerShell 5.1 parses a BOM-less UTF-8 script as ANSI,
# which mangles CJK literals (see scripts/_tools/shot-oplog.ps1 for the same note).
# All CJK text (tab labels, button captions) therefore lives in the UTF-8 step JSON,
# which is read here with an explicit UTF8 encoding.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File uidriver.ps1 `
#       -Steps steps\oplog-smoke.json -OutDir C:\Temp\autotest\run-001
#
# Exit code: 0 = every step ok, 1 = at least one step failed, 2 = fatal (bad input, no window).
#
# Step JSON shape (see ui/steps/*.json):
#   {
#     "name": "smoke",
#     "vars": { "lot": "ABC123" },              // optional; {{lot}} substituted in strings
#     "steps": [
#       { "action": "wait",         "target": { "name": "..." }, "timeoutMs": 8000 },
#       { "action": "click",        "target": { "automationId": "RbMh" } },
#       { "action": "settext",      "target": { "automationId": "LotNoBox" }, "value": "{{lot}}" },
#       { "action": "select",       "target": { "automationId": "ActionTypeBox" }, "value": "..." },
#       { "action": "clickpoint",   "x": 512, "y": 300 },          // window-relative logical px
#       { "action": "key",          "value": "{ENTER}" },
#       { "action": "screenshot",   "out": "01-page.png", "window": "main" },
#       { "action": "sleep",        "ms": 500 },
#       { "action": "assertexists", "target": { "namePrefix": "..." } },
#       { "action": "assertgone",   "target": { "name": "..." }, "timeoutMs": 3000 },
#       { "action": "dump",         "out": "tree.json", "max": 400 }
#     ]
#   }
#
# Target spec fields: automationId | name | namePrefix | controlType | index | scope
#   scope: "root" (default, whole desktop) | "foreground" (topmost window, for dialogs)
# Session 0 note: if every step fails with "element not found", this process is not in the
# interactive session - start it via a scheduled task ("run only when user is logged on").

param(
    [Parameter(Mandatory = $true)][string]$Steps,
    [string]$OutDir = ".",
    [string]$ProcessName = "WireCabinet.Hmi",
    [string]$WindowName = "",
    [string]$MarkerAutomationId = "RbOpLog",
    [string]$VarsFile = "",
    [int]$DefaultTimeoutMs = 10000,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$script:mainWindowEl = $null

try {
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Windows.Forms   # SendKeys (key typing / PasswordBox fallback)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
} catch {
    Write-Output ("FATAL: cannot load UIAutomation assemblies: " + $_.Exception.Message)
    exit 2
}

# --- Win32 helpers -----------------------------------------------------------

$w32 = @'
using System;
using System.Runtime.InteropServices;
public class W32D {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
Add-Type -TypeDefinition $w32

# --- logging / result --------------------------------------------------------

$script:results = New-Object System.Collections.ArrayList
$script:vars = @{}

function Write-Log($msg) {
    if (-not $Quiet) { Write-Output $msg }
}

function Add-StepResult($index, $action, $ok, $detail) {
    # NB: PowerShell 5.1 cannot parse "[ordered]@{...}" as a direct method argument,
    # so the row is built into a variable first.
    $row = [ordered]@{
        i      = $index
        action = $action
        ok     = $ok
        detail = $detail
    }
    [void]$script:results.Add($row)
    Write-Log (("  [{0}] {1} -> {2} {3}" -f $index, $action, $(if ($ok) { "ok" } else { "FAIL" }), $detail))
}

function Save-Result($path, $ok, $errorText) {
    $payload = [ordered]@{
        ok        = $ok
        error     = $errorText
        process   = $ProcessName
        startedAt = $script:startedAt
        endedAt   = (Get-Date).ToString('s')
        steps     = $script:results
    }
    $json = $payload | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

# --- text substitution -------------------------------------------------------

function Expand-Vars([string]$text) {
    if ($null -eq $text) { return $null }
    $out = $text
    foreach ($k in $script:vars.Keys) {
        $out = $out.Replace('{{' + $k + '}}', [string]$script:vars[$k])
    }
    return $out
}

# --- element lookup ----------------------------------------------------------

$controlTypeMap = @{
    'Button'     = [System.Windows.Automation.ControlType]::Button
    'Text'       = [System.Windows.Automation.ControlType]::Text
    'Edit'       = [System.Windows.Automation.ControlType]::Edit
    'ComboBox'   = [System.Windows.Automation.ControlType]::ComboBox
    'RadioButton'= [System.Windows.Automation.ControlType]::RadioButton
    'CheckBox'   = [System.Windows.Automation.ControlType]::CheckBox
    'List'       = [System.Windows.Automation.ControlType]::List
    'ListItem'   = [System.Windows.Automation.ControlType]::ListItem
    'Window'     = [System.Windows.Automation.ControlType]::Window
    'Pane'       = [System.Windows.Automation.ControlType]::Pane
    'Custom'     = [System.Windows.Automation.ControlType]::Custom
    'TabItem'    = [System.Windows.Automation.ControlType]::TabItem
    'Image'      = [System.Windows.Automation.ControlType]::Image
}

function Get-SearchRoot($spec) {
    if ($spec -and $spec.scope -eq 'foreground') {
        $fg = [W32D]::GetForegroundWindow()
        return [System.Windows.Automation.AutomationElement]::FromHandle($fg)
    }
    if ($spec -and $spec.scope -eq 'desktop') {
        return [System.Windows.Automation.AutomationElement]::RootElement
    }
    # Default: the HMI main window only. Scoping to the main window matters because a
    # second window of the same process (e.g. the flow-trace debug window) exposes
    # elements with identical names, and a desktop-wide search would match either one.
    $win = Get-MainWindowElement
    if ($null -ne $win) { return $win }
    return [System.Windows.Automation.AutomationElement]::RootElement
}

# Resolve the main window element by CONTENT, not by Process.MainWindowHandle: that
# property is z-order dependent and flips to the flow-trace debug window once it is
# raised, which silently redirects every find to a window that has no tabs.
# A window qualifies as the main window when it contains the marker element (the
# 存取记录 tab radio button, AutomationId RbOpLog), which exists on every page.
function Get-MainWindowElement {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $proc) { return $null }

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $cond)

    $windows = New-Object System.Collections.ArrayList
    foreach ($w in $wins) {
        try {
            if ($w.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
            if ($WindowName -and -not $w.Current.Name.StartsWith($WindowName)) { continue }
            [void]$windows.Add($w)
        } catch { }
    }
    if ($windows.Count -eq 0) { return $null }

    $markerCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $MarkerAutomationId)
    foreach ($w in $windows) {
        try {
            $hit = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $markerCond)
            if ($null -ne $hit) { return $w }
        } catch { }
    }

    if ($proc.MainWindowHandle -ne 0) {
        foreach ($w in $windows) {
            if ([IntPtr]$w.Current.NativeWindowHandle -eq [IntPtr]$proc.MainWindowHandle) { return $w }
        }
    }

    $best = $null
    $bestArea = -1
    foreach ($w in $windows) {
        try {
            $r = $w.Current.BoundingRectangle
            $area = [int]$r.Width * [int]$r.Height
            if ($area -gt $bestArea) { $bestArea = $area; $best = $w }
        } catch { }
    }
    return $best
}

function Find-ElementOnce($spec) {
    $root = Get-SearchRoot $spec
    if ($null -eq $root) { return $null }

    $conds = New-Object System.Collections.ArrayList
    if ($spec.automationId) {
        [void]$conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, [string]$spec.automationId)))
    }
    if ($spec.name) {
        [void]$conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, (Expand-Vars ([string]$spec.name)))))
    }
    if ($spec.controlType -and $controlTypeMap.ContainsKey([string]$spec.controlType)) {
        [void]$conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlTypeMap[[string]$spec.controlType])))
    }
    if (-not ($spec.automationId -or $spec.name -or $spec.namePrefix -or $spec.controlType)) {
        throw "target spec needs automationId, name, namePrefix or controlType"
    }

    $cond = $null
    if ($conds.Count -eq 0) {
        # namePrefix-only spec: no UIA property condition to pre-filter with, scan all descendants.
        $cond = [System.Windows.Automation.Condition]::TrueCondition
    } elseif ($conds.Count -eq 1) {
        $cond = $conds[0]
    } else {
        $cond = New-Object System.Windows.Automation.AndCondition($conds.ToArray())
    }

    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $candidates = New-Object System.Collections.ArrayList
    foreach ($el in $all) {
        if ($spec.namePrefix) {
            $n = $el.Current.Name
            $p = Expand-Vars ([string]$spec.namePrefix)
            if ($null -eq $n -or -not $n.StartsWith($p)) { continue }
        }
        [void]$candidates.Add($el)
    }
    if ($candidates.Count -eq 0) { return $null }
    # Pages reuse the same x:Name (WireLotBox exists on both the MH and the OP view,
    # StatusText on every view plus the status bar), so prefer elements that are actually
    # on screen; a hidden counterpart cannot take focus and ValuePattern silently no-ops.
    if (-not $spec.includeOffscreen) {
        $visible = @($candidates | Where-Object { -not $_.Current.IsOffscreen })
        if ($visible.Count -gt 0) { $candidates = $visible }
    }
    $idx = 0
    if ($spec.index) { $idx = [int]$spec.index }
    if ($idx -ge $candidates.Count) { return $null }
    return $candidates[$idx]
}

function Find-Element($spec, $timeoutMs) {
    if ($timeoutMs -le 0) { $timeoutMs = $DefaultTimeoutMs }
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    do {
        $el = Find-ElementOnce $spec
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Describe-Target($spec) {
    $bits = @()
    if ($spec.automationId) { $bits += ("id=" + $spec.automationId) }
    if ($spec.name) { $bits += ("name=" + $spec.name) }
    if ($spec.namePrefix) { $bits += ("namePrefix=" + $spec.namePrefix) }
    if ($spec.controlType) { $bits += ("type=" + $spec.controlType) }
    if ($spec.index) { $bits += ("index=" + $spec.index) }
    return ($bits -join " ")
}

function Describe-Element($el) {
    $c = $el.Current
    if (-not [string]::IsNullOrWhiteSpace($c.Name)) { return ("name=" + $c.Name) }
    if (-not [string]::IsNullOrWhiteSpace($c.AutomationId)) { return ("id=" + $c.AutomationId) }
    return ("type=" + ($c.ControlType.ProgrammaticName -replace 'ControlType\.', ''))
}

# --- actions -----------------------------------------------------------------

function Get-MainWindowInfo {
    # Derived from the resolved main-window element, not Process.MainWindowHandle, so that
    # focus and screenshots always target the HMI window and not a debug/dialog window.
    $win = Get-MainWindowElement
    if ($null -eq $win) { return $null }
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $proc) { return $null }
    return @{ Handle = [IntPtr]$win.Current.NativeWindowHandle; Process = $proc }
}

function Focus-MainWindow($info) {
    # A minimized window still reports a tiny rect (e.g. 160x28 on the cabinet), which makes
    # every screenshot useless and every coordinate click land in the wrong place. Restore it
    # first; SW_RESTORE(9) also un-maximizes, which is what we want for a fixed 1024x768 UI.
    if ([W32D]::IsIconic($info.Handle)) {
        [void][W32D]::ShowWindow($info.Handle, 9)
        Start-Sleep -Milliseconds 500
    }
    [void][W32D]::SetForegroundWindow($info.Handle)
    Start-Sleep -Milliseconds 350
}

# Try UIA patterns first (session-independent, no cursor warp); fall back to a real
# mouse click at the element's clickable point (needed for Border/TextBlock slot tiles,
# which expose no InvokePattern).
function Invoke-ClickElement($el) {
    foreach ($pattern in @(
            @{ Name = 'InvokePattern';         Id = [System.Windows.Automation.InvokePattern]::Pattern },
            @{ Name = 'SelectionItemPattern';  Id = [System.Windows.Automation.SelectionItemPattern]::Pattern },
            @{ Name = 'TogglePattern';         Id = [System.Windows.Automation.TogglePattern]::Pattern })) {
        try {
            $obj = $el.GetCurrentPattern($pattern.Id)
            if ($null -ne $obj) {
                switch ($pattern.Name) {
                    'InvokePattern'        { $obj.Invoke() }
                    'SelectionItemPattern' { $obj.Select() }
                    'TogglePattern'        { $obj.Toggle() }
                }
                Start-Sleep -Milliseconds 250
                return ("pattern:" + $pattern.Name)
            }
        } catch { }
    }
    # coordinate fallback
    try { $el.SetFocus() } catch { }
    $pt = $el.GetClickablePoint()
    [void][W32D]::SetCursorPos([int]$pt.X, [int]$pt.Y)
    Start-Sleep -Milliseconds 120
    [W32D]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    [W32D]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    return "point"
}

function Set-ElementText($el, $value, $forceKeys = $false, $useClipboard = $false) {
    if (-not $forceKeys -and -not $useClipboard) {
        try {
            $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            if ($null -ne $vp) {
                $vp.SetValue([string]$value)
                Start-Sleep -Milliseconds 200
                return "value"
            }
        } catch { }
    }
    # Fallbacks, for controls whose ValuePattern does not register (PasswordBox, the native
    # file dialog's name box) or where keystrokes get mangled by the input method: with a
    # Chinese keyboard layout SendKeys turns every '\' into '、', so paths must be pasted
    # from the clipboard instead of typed.
    $el.SetFocus()
    Start-Sleep -Milliseconds 200
    if ($useClipboard) {
        Set-Clipboard -Value ([string]$value)
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait("^a")
        Start-Sleep -Milliseconds 60
        [System.Windows.Forms.SendKeys]::SendWait("^v")
        Start-Sleep -Milliseconds 250
        return "clipboard"
    }
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    Start-Sleep -Milliseconds 60
    [System.Windows.Forms.SendKeys]::SendWait([string]$value)
    Start-Sleep -Milliseconds 200
    return "keys"
}

function Select-ComboItem($el, $value) {
    try {
        $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($null -ne $vp) {
            $vp.SetValue([string]$value)
            Start-Sleep -Milliseconds 300
            return "value"
        }
    } catch { }
    $expand = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 400
    $itemCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, [string]$value)
    $item = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
    if ($null -eq $item) {
        $expand.Collapse()
        throw ("combo item not found: " + $value)
    }
    $sel = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    Start-Sleep -Milliseconds 300
    return "item"
}

function Save-Screenshot($outFile, $mode, $info) {
    $h = $info.Handle
    if ($mode -eq 'foreground') {
        $h = [W32D]::GetForegroundWindow()
    }
    $r = New-Object W32D+RECT
    [void][W32D]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left
    $ht = $r.Bottom - $r.Top
    if ($w -le 0 -or $ht -le 0) { throw "bad window rect $w x $ht" }

    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try {
        # PW_RENDERFULLCONTENT (2): window renders itself into the bitmap, so we do not
        # capture whatever happens to be stacked on top at those screen coordinates.
        [void][W32D]::PrintWindow($h, $hdc, 2)
    } finally {
        $g.ReleaseHdc($hdc)
    }
    $path = Join-Path $OutDir $outFile
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    return ("$outFile " + $w + "x" + $ht)
}

function Dump-Tree($outFile, $max, $scope) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    if ($scope -ne 'desktop') {
        $win = Get-MainWindowElement
        if ($null -ne $win) { $root = $win }
    }
    if ($scope -eq 'foreground') {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([W32D]::GetForegroundWindow())
    }
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $rows = New-Object System.Collections.ArrayList
    $n = 0
    foreach ($el in $all) {
        if ($n -ge $max) { break }
        $c = $el.Current
        if ([string]::IsNullOrWhiteSpace($c.AutomationId) -and [string]::IsNullOrWhiteSpace($c.Name)) { continue }
        $row = [ordered]@{
            type = $c.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            id   = $c.AutomationId
            name = $c.Name
        }
        [void]$rows.Add($row)
        $n++
    }
    $json = $rows | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText((Join-Path $OutDir $outFile), $json, (New-Object System.Text.UTF8Encoding($false)))
    return ("$outFile (" + $rows.Count + " entries)")
}

# --- main --------------------------------------------------------------------

$script:startedAt = (Get-Date).ToString('s')
if (-not (Test-Path $OutDir)) { [void](New-Item -ItemType Directory -Path $OutDir -Force) }
$resultPath = Join-Path $OutDir 'result.json'

$raw = [System.IO.File]::ReadAllText((Resolve-Path $Steps).Path, [System.Text.Encoding]::UTF8)
$plan = $raw | ConvertFrom-Json

if ($plan.vars) {
    foreach ($p in $plan.vars.PSObject.Properties) { $script:vars[$p.Name] = $p.Value }
}
# Machine-local vars (site passwords, lot numbers) come from a separate file kept out of
# version control; they override the scenario's own values.
if ($VarsFile) {
    $varsPath = (Resolve-Path $VarsFile).Path
    $localVars = ([System.IO.File]::ReadAllText($varsPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json)
    foreach ($p in $localVars.PSObject.Properties) { $script:vars[$p.Name] = $p.Value }
    Write-Log ("vars file: " + $varsPath + " (" + ($localVars.PSObject.Properties.Name -join ", ") + ")")
}

$info = Get-MainWindowInfo
if ($null -eq $info) {
    Save-Result $resultPath $false ("process not running or has no main window: " + $ProcessName)
    Write-Output ("FATAL: " + $ProcessName + " not running (or no main window). If it IS running, this process is probably in session 0 - start it through a scheduled task.")
    exit 2
}

Focus-MainWindow $info
Write-Log ("driver: process=" + $ProcessName + " hwnd=" + $info.Handle + " steps=" + $plan.steps.Count)
if ($plan.name) { Write-Log ("scenario: " + $plan.name) }

$index = 0
foreach ($step in $plan.steps) {
    $index++
    $action = [string]$step.action
    try {
        switch ($action) {
            'sleep' {
                $ms = if ($step.ms) { [int]$step.ms } else { 500 }
                Start-Sleep -Milliseconds $ms
                Add-StepResult $index $action $true ("slept " + $ms + "ms")
            }
            'wait' {
                if (-not $step.target) { throw "wait needs target" }
                $el = Find-Element $step.target $(if ($step.timeoutMs) { [int]$step.timeoutMs } else { $DefaultTimeoutMs })
                if ($null -eq $el) { throw ("element not found: " + (Describe-Target $step.target)) }
                Add-StepResult $index $action $true (Describe-Target $step.target)
            }
            'click' {
                if (-not $step.target) { throw "click needs target" }
                # A modal gate disables its owner. Switching tabs cannot dismiss it.
                if ($step.target.automationId -like 'Rb*') {
                    $main = Get-MainWindowElement
                    $process = Get-Process -Name $ProcessName | Select-Object -First 1
                    $passwordCondition = New-Object System.Windows.Automation.AndCondition(
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)),
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PasswordBox')))
                    $passwordBox = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                        [System.Windows.Automation.TreeScope]::Descendants, $passwordCondition)
                    if ($null -ne $passwordBox -or ($null -ne $main -and -not $main.Current.IsEnabled)) {
                        throw 'tab click blocked by modal dialog; cancel or unlock the current dialog first'
                    }
                }
                $el = Find-Element $step.target $(if ($step.timeoutMs) { [int]$step.timeoutMs } else { $DefaultTimeoutMs })
                if ($null -eq $el) { throw ("element not found: " + (Describe-Target $step.target)) }
                $how = Invoke-ClickElement $el
                Add-StepResult $index $action $true ((Describe-Target $step.target) + " via " + $how)
            }
            'settext' {
                if (-not $step.target) { throw "settext needs target" }
                $el = Find-Element $step.target $(if ($step.timeoutMs) { [int]$step.timeoutMs } else { $DefaultTimeoutMs })
                if ($null -eq $el) { throw ("element not found: " + (Describe-Target $step.target)) }
                $value = Expand-Vars ([string]$step.value)
                $how = Set-ElementText $el $value ([bool]$step.useKeys) ([bool]$step.useClipboard)
                Add-StepResult $index $action $true ((Describe-Target $step.target) + " via " + $how)
            }
            'select' {
                if (-not $step.target) { throw "select needs target" }
                $el = Find-Element $step.target $(if ($step.timeoutMs) { [int]$step.timeoutMs } else { $DefaultTimeoutMs })
                if ($null -eq $el) { throw ("element not found: " + (Describe-Target $step.target)) }
                $how = Select-ComboItem $el (Expand-Vars ([string]$step.value))
                Add-StepResult $index $action $true ((Expand-Vars ([string]$step.value)) + " via " + $how)
            }
            'clickpoint' {
                Focus-MainWindow $info
                $r = New-Object W32D+RECT
                [void][W32D]::GetWindowRect($info.Handle, [ref]$r)
                # x/y are window-relative physical pixels (the same space GetClickablePoint reports).
                $x = $r.Left + [int]$step.x
                $y = $r.Top + [int]$step.y
                [void][W32D]::SetCursorPos($x, $y)
                Start-Sleep -Milliseconds 150
                [W32D]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
                [W32D]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
                Start-Sleep -Milliseconds 300
                Add-StepResult $index $action $true ("clicked at window " + $step.x + "," + $step.y + " (abs " + $x + "," + $y + ")")
            }
            'key' {
                # Never submit a dialog after its input step was skipped or failed.
                # Otherwise Enter can activate a focused button on the product page.
                if ($script:results.Count -gt 0) {
                    $previous = $script:results[$script:results.Count - 1]
                    if ($previous.action -eq 'settext' -and -not $previous.ok) {
                        throw 'key skipped because the preceding settext did not succeed'
                    }
                }
                # Deliberately does NOT call Focus-MainWindow: that would pull focus out of a
                # modal dialog (password prompt, native save dialog) and the keystroke would
                # land on the main window instead of the control the step just filled.
                [System.Windows.Forms.SendKeys]::SendWait([string]$step.value)
                Start-Sleep -Milliseconds 250
                Add-StepResult $index $action $true ([string]$step.value)
            }
            'screenshot' {
                $out = if ($step.out) { [string]$step.out } else { ("step-" + $index + ".png") }
                $mode = if ($step.window) { [string]$step.window } else { 'main' }
                $detail = Save-Screenshot $out $mode $info
                Add-StepResult $index $action $true $detail
            }
            'assertexists' {
                if (-not $step.target) { throw "assertexists needs target" }
                $timeout = if ($step.timeoutMs) { [int]$step.timeoutMs } else { 3000 }
                $el = Find-Element $step.target $timeout
                if ($null -eq $el) { throw ("expected to find: " + (Describe-Target $step.target)) }
                Add-StepResult $index $action $true ("found " + (Describe-Element $el))
            }
            'assertgone' {
                if (-not $step.target) { throw "assertgone needs target" }
                $timeout = if ($step.timeoutMs) { [int]$step.timeoutMs } else { 3000 }
                $deadline = (Get-Date).AddMilliseconds($timeout)
                $still = $true
                do {
                    $el = Find-ElementOnce $step.target
                    if ($null -eq $el) { $still = $false; break }
                    Start-Sleep -Milliseconds 250
                } while ((Get-Date) -lt $deadline)
                if ($still) { throw ("still present after " + $timeout + "ms: " + (Describe-Target $step.target)) }
                Add-StepResult $index $action $true ("gone: " + (Describe-Target $step.target))
            }
            'dump' {
                $out = if ($step.out) { [string]$step.out } else { ("tree-" + $index + ".json") }
                $max = if ($step.max) { [int]$step.max } else { 400 }
                $scope = if ($step.scope) { [string]$step.scope } else { 'main' }
                $detail = Dump-Tree $out $max $scope
                Add-StepResult $index $action $true $detail
            }
            'asserttext' {
                # Asserts on an element's Name (caption) - the way to check UI state textually,
                # e.g. the log page summary switching to/from the "no matching records" caption.
                if (-not $step.target) { throw "asserttext needs target" }
                $el = Find-Element $step.target $(if ($step.timeoutMs) { [int]$step.timeoutMs } else { $DefaultTimeoutMs })
                if ($null -eq $el) { throw ("element not found: " + (Describe-Target $step.target)) }
                $actual = $el.Current.Name
                $expect = Expand-Vars ([string]$step.value)
                $mode = if ($step.mode) { [string]$step.mode } else { 'contains' }
                $hit = $false
                switch ($mode) {
                    'contains' { $hit = ($actual -ne $null -and $actual.Contains($expect)) }
                    'equals'   { $hit = ($actual -eq $expect) }
                    'prefix'   { $hit = ($actual -ne $null -and $actual.StartsWith($expect)) }
                    'regex'    { $hit = ($actual -match $expect) }
                    default    { throw ("unknown asserttext mode: " + $mode) }
                }
                if (-not $hit) {
                    throw ("text assertion failed: " + $mode + " '" + $expect + "' but actual was '" + $actual + "'")
                }
                Add-StepResult $index $action $true ($mode + " '" + $expect + "' in '" + $actual + "'")
            }
            default { throw ("unknown action: " + $action) }
        }
    } catch {
        if ($step.optional) {
            # Used where the environment may or may not require a step (e.g. the password
            # dialog exists only when the site configured access-gate passwords).
            Add-StepResult $index $action $false ("(optional, ignored) " + $_.Exception.Message)
            continue
        }
        Add-StepResult $index $action $false $_.Exception.Message
        Save-Result $resultPath $false ("step " + $index + " (" + $action + ") failed")
        Write-Output ("FAIL at step " + $index + ": " + $action + " - " + $_.Exception.Message)
        exit 1
    }
}

Save-Result $resultPath $true $null
Write-Output ("driver: all " + $index + " steps ok, result -> " + $resultPath)
exit 0
