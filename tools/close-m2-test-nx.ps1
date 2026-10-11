# Normal exit of only the recorded dedicated NX, discarding only its test copy.
param(
    [Parameter(Mandatory=$true)][string]$LaunchReport,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$SavedPartPath = ''
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { throw 'Refusing to overwrite exit evidence.' }
$launch = Get-Content -LiteralPath $LaunchReport -Raw -Encoding UTF8 | ConvertFrom-Json
$expectedPartPath = [IO.Path]::GetFullPath($launch.copied_part)
if (-not [string]::IsNullOrWhiteSpace($SavedPartPath)) {
    $savedPath = [IO.Path]::GetFullPath($SavedPartPath)
    if ($launch.test_stage -notin @('M2-7','M3','M4','M5','M6') -or
        [IO.Path]::GetDirectoryName($savedPath) -ne [IO.Path]::GetDirectoryName($expectedPartPath) -or
        [IO.Path]::GetExtension($savedPath) -ne '.prt' -or
        -not (Test-Path -LiteralPath $savedPath -PathType Leaf)) {
        throw 'Saved test copy must exist within the recorded M2-7/M3/M4/M5/M6 part directory.'
    }
    $expectedPartPath = $savedPath
}
$process = Get-Process -Id $launch.pid
$started = if ($launch.process_start_utc -is [DateTime]) { $launch.process_start_utc.ToUniversalTime() }
    else { [DateTimeOffset]::Parse([string]$launch.process_start_utc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime }
if ($process.ProcessName -ne 'ugraf' -or $process.Path -ne $launch.exe -or
    [Math]::Abs(($process.StartTime.ToUniversalTime() - $started).TotalMilliseconds) -gt 1) {
    throw 'Recorded dedicated NX identity differs.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class M2ExitNative {
 public const string ExitName = "\u9000\u51fa";
 public const string NoExitName = "\u5426 - \u9000\u51fa(N)";
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
 [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 public static void ChooseNoExit(IntPtr dialog,IntPtr button) { SendMessage(dialog,0x111,(IntPtr)2,button); }
}
'@
$owned = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
$dialogCondition = [System.Windows.Automation.AndCondition]::new($owned,
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,[M2ExitNative]::ExitName))
$root = [System.Windows.Automation.AutomationElement]::RootElement
$dialog = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$dialogCondition)
if ($null -eq $dialog) {
    $processWindows = $root.FindAll([System.Windows.Automation.TreeScope]::Children,$owned)
    $mainWindows = @(foreach ($window in $processWindows) {
        if ($window.Current.Name.StartsWith('Designcenter')) { $window }
    })
    if ($mainWindows.Count -ne 1) { throw 'Expected one dedicated NX main window for normal exit.' }
    $mainHandle = [IntPtr]$mainWindows[0].Current.NativeWindowHandle
    [uint32]$mainPid = 0
    [void][M2ExitNative]::GetWindowThreadProcessId($mainHandle,[ref]$mainPid)
    if ($mainPid -ne $process.Id -or
        -not [M2ExitNative]::PostMessage($mainHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero)) {
        throw 'Normal close request to the identified NX window was not accepted.'
    }
    Start-Sleep -Milliseconds 800
    $dialog = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$dialogCondition)
}
$discarded = $false
if ($null -ne $dialog) {
    $items = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    $notices = @(foreach ($item in $items) { if ($item.Current.AutomationId -eq '1002') { $item.Current.Name } })
    $buttons = @(foreach ($item in $items) {
        if ($item.Current.AutomationId -eq '2' -and $item.Current.Name -eq [M2ExitNative]::NoExitName -and $item.Current.IsEnabled) { $item }
    })
    $leaf = [IO.Path]::GetFileName($expectedPartPath)
    $partNames = @($notices -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '(?i)\.prt$' })
    if ($notices.Count -ne 1 -or $buttons.Count -ne 1 -or $partNames.Count -ne 1 -or $partNames[0] -ne $leaf) {
        throw 'Exit dialog does not identify only the recorded test copy; no discard submitted.'
    }
    $dialogHandle = [IntPtr]$dialog.Current.NativeWindowHandle
    $buttonHandle = [IntPtr]$buttons[0].Current.NativeWindowHandle
    [uint32]$dialogPid = 0
    [void][M2ExitNative]::GetWindowThreadProcessId($dialogHandle,[ref]$dialogPid)
    if ($dialogPid -ne $process.Id -or [M2ExitNative]::GetParent($buttonHandle) -ne $dialogHandle) {
        throw 'Exit control process or parent identity differs; no discard submitted.'
    }
    [M2ExitNative]::ChooseNoExit($dialogHandle,$buttonHandle)
    $discarded = $true
}
$timer = [Diagnostics.Stopwatch]::StartNew()
while ((Get-Process -Id $process.Id -ErrorAction SilentlyContinue) -and $timer.ElapsedMilliseconds -lt 8000) {
    Start-Sleep -Milliseconds 100
}
$exited = -not [bool](Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
$record = [ordered]@{
    captured_utc=[DateTime]::UtcNow.ToString('o'); pid=$process.Id; normal_exit=$exited;
    discarded_exclusively_created_test_part=($discarded -and $exited);
    expected_leaf=[IO.Path]::GetFileName($expectedPartPath); force_terminated=$false
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($record | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$record | ConvertTo-Json
if (-not $exited) { throw 'Dedicated NX has not exited; no forced termination performed.' }
