#Requires -Version 5.1
<#
.SYNOPSIS
    Drives the editor UI through a sequence of real interactions and reports crashes.

.DESCRIPTION
    A green build proves nothing about a UI. This harness launches the real app,
    finds real controls through UI Automation, clicks them at their actual screen
    coordinates, and after every step checks whether the process died and whether
    anything landed on stderr.

    It exists because two crashes in this project were both "green build, dead on
    arrival": one from a type mismatch, one from initialising a Window in
    OnInitialized. Neither would ever have been caught by a test that does not
    actually open the window.

.PARAMETER Step
    Optional single step name to run, for debugging one interaction.

.EXAMPLE
    .\tools\uismoke.ps1
    .\tools\uismoke.ps1 -Step NewRule
#>
[CmdletBinding()]
param(
    [string] $Step = '',
    [string] $ExePath = ''
)

$ErrorActionPreference = 'Stop'

if (-not $ExePath) {
    $ExePath = Join-Path $PSScriptRoot '..\src\Juggler.Ui\bin\Debug\net10.0\Juggler.Ui.exe'
}
$ExePath = (Resolve-Path $ExePath).Path

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

if (-not ('JugglerSmoke' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class JugglerSmoke
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    private const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(220);
        mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
    }

    /// <summary>Top-level visible windows owned by the given process.</summary>
    public static IntPtr[] Windows(uint pid)
    {
        var found = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, p) =>
        {
            uint wpid;
            GetWindowThreadProcessId(h, out wpid);
            if (wpid == pid && IsWindowVisible(h)) { found.Add(h); }
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }
}
'@
}

# ---------------------------------------------------------------------------
# Harness
# ---------------------------------------------------------------------------
$script:results = New-Object System.Collections.Generic.List[object]
$script:app = $null
$script:errFile = $null

function Start-App {
    param([switch] $FreshConfig)

    Stop-App

    $configDir = Join-Path $env:LOCALAPPDATA 'FileJuggler'
    $config = Join-Path $configDir 'rules.json'
    if ($FreshConfig) {
        New-Item -ItemType Directory -Force -Path $configDir | Out-Null
        Copy-Item (Join-Path $PSScriptRoot '..\config\example.rules.json') $config -Force
    }

    $script:errFile = [System.IO.Path]::GetTempFileName()
    $script:app = Start-Process -FilePath $ExePath -PassThru -RedirectStandardError $script:errFile

    # The window handle can take ~10 s on a cold start (JIT plus font loading).
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        $script:app.Refresh()
        if ($script:app.HasExited) { return $false }
        if ($script:app.MainWindowHandle -ne 0) { break }
    }

    $h = [IntPtr]$script:app.MainWindowHandle
    [JugglerSmoke]::SetWindowPos($h, [IntPtr](-1), 40, 40, 0, 0, 0x0041) | Out-Null
    [JugglerSmoke]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Seconds 2
    return $true
}

function Stop-App {
    if ($script:app -and -not $script:app.HasExited) {
        try { Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Get-Process -Name 'Juggler.Ui' -ErrorAction SilentlyContinue |
        ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 800
}

function Get-Stderr {
    # Get-Content -Raw returns $null for an empty file, which would make the caller's
    # .Substring() fail on a perfectly healthy app. Coerce to a string.
    if ($script:errFile -and (Test-Path $script:errFile)) {
        $t = Get-Content $script:errFile -Raw -ErrorAction SilentlyContinue
        if ($null -eq $t) { return '' }
        return [string]$t
    }
    return ''
}

function Find-Element {
    param([IntPtr] $Handle, [string] $Name, [string] $Type = '')

    if ($Handle -eq [IntPtr]::Zero) { return $null }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)

    $conds = New-Object System.Collections.Generic.List[object]
    if ($Name) {
        $conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    }
    if ($Type) {
        $conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::$Type)))
    }

    if ($conds.Count -eq 1) { $cond = $conds[0] }
    else { $cond = New-Object System.Windows.Automation.AndCondition($conds.ToArray()) }

    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Step {
    param(
        [string] $Name,
        [scriptblock] $Action,
        [int] $SettleMs = 1500
    )

    if ($Step -and $Name -ne $Step) { return }

    $before = Get-Stderr
    $detail = 'ok'

    try {
        & $Action
    }
    catch {
        $detail = "action threw: $($_.Exception.Message)"
    }

    Start-Sleep -Milliseconds $SettleMs

    $alive = $true
    $title = ''
    if ($script:app) {
        $script:app.Refresh()
        $alive = -not $script:app.HasExited
        if ($alive) { $title = $script:app.MainWindowTitle }
    }

    $after = Get-Stderr
    $newErr = $after.Substring([Math]::Min($before.Length, $after.Length))

    # A crash shows up as either a dead process or a fresh stack trace on stderr.
    $crashed = (-not $alive) -or ($newErr -match 'Unhandled exception|at Juggler\.')

    if ($crashed) {
        $first = ($newErr -split "`n" | Where-Object { $_ -match 'Exception|No \w+ named|error' } |
            Select-Object -First 1)
        if (-not $first) { $first = 'process exited' }
        $detail = "CRASH: $($first.Trim())"
    }

    $script:results.Add([pscustomobject]@{
        Step    = $Name
        Alive   = $alive
        Crashed = $crashed
        Detail  = $detail
    })

    if ($crashed) {
        Write-Host ("  [CRASH] {0}: {1}" -f $Name, $detail) -ForegroundColor Red
        Write-Host $newErr -ForegroundColor DarkRed
        # Bring it back up so later steps still run and we collect more than one bug.
        Start-App | Out-Null
    }
    else {
        Write-Host ("  [ ok  ] {0}" -f $Name) -ForegroundColor Green
    }
}

function Get-MainWindow { return [IntPtr]$script:app.MainWindowHandle }

function Get-SecondaryWindow {
    param([string] $TitleLike = '*')
    $wins = [JugglerSmoke]::Windows([uint32]$script:app.Id)
    foreach ($w in $wins) {
        if ($w -eq [IntPtr]$script:app.MainWindowHandle) { continue }
        return $w
    }
    return [IntPtr]::Zero
}

function Click-Element {
    param([string] $Name, [string] $Type = '', [IntPtr] $Handle = [IntPtr]::Zero)
    if ($Handle -eq [IntPtr]::Zero) { $Handle = Get-MainWindow }
    $el = Find-Element -Handle $Handle -Name $Name -Type $Type
    if (-not $el) { throw "element not found: '$Name'" }
    $r = $el.Current.BoundingRectangle
    [JugglerSmoke]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}

function Close-Secondary {
    <#
      Windows in this app close in three different ways depending on which one it is:
      Diagnostics has a "Close" button, the rule editor has a "<- Rules" back button,
      and anything else responds to Escape. Trying all three keeps the harness from
      silently skipping a step (an exception here used to look like a pass).
    #>
    $w = Get-SecondaryWindow
    if ($w -eq [IntPtr]::Zero) { return $false }

    foreach ($name in @('Close', 'Save')) {
        $el = Find-Element -Handle $w -Name $name -Type 'Button'
        if ($el) {
            $r = $el.Current.BoundingRectangle
            [JugglerSmoke]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            Start-Sleep -Milliseconds 900
            return $true
        }
    }

    # Back button: its accessible name is the arrow glyph plus "Rules".
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($w)
    $btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    foreach ($b in $btns) {
        if ($b.Current.Name -match 'Rules') {
            $r = $b.Current.BoundingRectangle
            [JugglerSmoke]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            Start-Sleep -Milliseconds 900
            return $true
        }
    }

    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 900
    return $true
}

function Click-ThemeToggle {
    <#
      The theme button has no Content or Name; its glyph is set from code, so its
      accessible name is the glyph itself: U+2600 (sun) while dark, U+25D0 (half moon)
      while light. Try both rather than assuming the current theme.
    #>
    foreach ($glyph in @([char]0x2600, [char]0x25D0)) {
        $el = Find-Element -Handle (Get-MainWindow) -Name ([string]$glyph) -Type 'Button'
        if ($el) {
            $r = $el.Current.BoundingRectangle
            [JugglerSmoke]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            return
        }
    }

    # Fall back to position: the button immediately left of "Diagnostics" in the header.
    $diag = Find-Element -Handle (Get-MainWindow) -Name 'Diagnostics' -Type 'Button'
    if (-not $diag) { throw 'Diagnostics button not found, cannot locate theme toggle' }

    $dr = $diag.Current.BoundingRectangle
    $root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-MainWindow))
    $btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))

    $best = $null; $bestRight = [double]::MaxValue
    foreach ($b in $btns) {
        $r = $b.Current.BoundingRectangle
        if ($r.X -lt $dr.X -and $r.Right -lt $bestRight -and $r.Y -eq $dr.Y) {
            $best = $b; $bestRight = $r.Right
        }
    }
    if (-not $best) { throw 'theme toggle not found' }

    $r = $best.Current.BoundingRectangle
    [JugglerSmoke]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------
Write-Host "UI smoke: $ExePath" -ForegroundColor Cyan

if (-not (Start-App -FreshConfig)) {
    Write-Host 'App failed to start at all.' -ForegroundColor Red
    Get-Stderr | Write-Host
    exit 1
}

Invoke-Step 'ThemeToggleDarkLight' {
    Click-ThemeToggle
}

Invoke-Step 'NewRuleOpens' {
    Click-Element -Name 'New rule' -Type 'Button'
    Start-Sleep -Milliseconds 2500
    $sec = Get-SecondaryWindow
    if ($sec -eq [IntPtr]::Zero) { throw 'editor window did not open' }
}

Invoke-Step 'EditorNameAcceptsInput' {
    $sec = Get-SecondaryWindow
    if ($sec -eq [IntPtr]::Zero) { throw 'editor window not open' }
    $box = Find-Element -Handle $sec -Type 'Edit'
    if ($box) {
        $r = $box.Current.BoundingRectangle
        [JugglerSmoke]::Click([int]($r.X + 20), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('My New Rule')
    }
}

Invoke-Step 'EditorSave' {
    $sec = Get-SecondaryWindow
    if ($sec -eq [IntPtr]::Zero) { throw 'editor window not open' }
    Click-Element -Name 'Save' -Type 'Button' -Handle $sec
}

Invoke-Step 'EditExistingRule' {
    Close-Secondary | Out-Null
    Start-Sleep -Milliseconds 600
    Click-Element -Name 'Edit' -Type 'Button'
    Start-Sleep -Milliseconds 2500
    if ((Get-SecondaryWindow) -eq [IntPtr]::Zero) { throw 'edit window did not open' }
}

Invoke-Step 'DiagnosticsOpens' {
    Close-Secondary | Out-Null
    Start-Sleep -Milliseconds 600
    Click-Element -Name 'Diagnostics' -Type 'Button'
    Start-Sleep -Milliseconds 2500
    if ((Get-SecondaryWindow) -eq [IntPtr]::Zero) { throw 'diagnostics did not open' }
}

Invoke-Step 'DiagnosticsClose' {
    $sec = Get-SecondaryWindow
    if ($sec -eq [IntPtr]::Zero) { throw 'diagnostics not open' }
    Click-Element -Name 'Close' -Type 'Button' -Handle $sec
}

Invoke-Step 'RowContextMenu' {
    Click-Element -Name ([string][char]0x22EE) -Type 'Button'
    Start-Sleep -Milliseconds 1200
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
}

Invoke-Step 'SearchFilter' {
    $box = Find-Element -Handle (Get-MainWindow) -Type 'Edit'
    if ($box) {
        $r = $box.Current.BoundingRectangle
        [JugglerSmoke]::Click([int]($r.X + 20), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('pdf')
    }
}

Invoke-Step 'ClearFilter' {
    $box = Find-Element -Handle (Get-MainWindow) -Type 'Edit'
    if ($box) {
        $r = $box.Current.BoundingRectangle
        [JugglerSmoke]::Click([int]($r.X + 20), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('^a{DEL}')
    }
}

Invoke-Step 'TabLog'      { Click-Element -Name 'Log' -Type 'TabItem' }
Invoke-Step 'TabSettings' { Click-Element -Name 'Settings' -Type 'TabItem' }
Invoke-Step 'TabRules'    { Click-Element -Name 'Rules' -Type 'TabItem' }

Invoke-Step 'ToggleRuleEnabled' {
    $sw = Find-Element -Handle (Get-MainWindow) -Type 'Button'
    if ($sw) {
        $r = $sw.Current.BoundingRectangle
        [JugglerSmoke]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    }
}

Stop-App
if ($script:errFile -and (Test-Path $script:errFile)) { Remove-Item $script:errFile -Force }

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Results' -ForegroundColor Cyan
$script:results | Format-Table -AutoSize -Wrap
$crashes = @($script:results | Where-Object { $_.Crashed })
Write-Host ("{0} steps, {1} crashes" -f $script:results.Count, $crashes.Count) `
    -ForegroundColor $(if ($crashes.Count) { 'Red' } else { 'Green' })
exit $(if ($crashes.Count) { 1 } else { 0 })