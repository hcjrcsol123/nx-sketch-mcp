# UI actions are scoped to the exact process created by launch-m2-read-test.ps1.
param(
    [Parameter(Mandatory=$true)][string]$LaunchReport,
    [Parameter(Mandatory=$true)][ValidateSet('Capture','InspectUI','Screenshot','OpenPart','Click','OpenLoad','OpenLoadMenu','Load','Close')][string]$Action,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$ImagePath = '',
    [string]$SavedPartPath = '',
    [int]$X = 0,
    [int]$Y = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class NxM2Dpi {
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h,out uint pid);
}
'@
[void][NxM2Dpi]::SetProcessDPIAware()
$launch = Get-Content -Raw -Encoding UTF8 -LiteralPath $LaunchReport | ConvertFrom-Json
$process = Get-Process -Id $launch.pid
$launchStartedUtc = if ($launch.process_start_utc -is [DateTime]) { $launch.process_start_utc.ToUniversalTime() }
    else { [DateTimeOffset]::Parse([string]$launch.process_start_utc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime }
if ($process.ProcessName -ne 'ugraf' -or $process.Path -ne $launch.exe -or
    [Math]::Abs(($process.StartTime.ToUniversalTime() - $launchStartedUtc).TotalMilliseconds) -gt 1) {
    throw 'Dedicated process identity differs from launch evidence.'
}
$scripts = Join-Path $PSScriptRoot 'nx_ui'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$nxWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))
$nxMain = @(foreach ($window in $nxWindows) { if ($window.Current.Name.StartsWith('Designcenter')) { $window } })
if ($nxMain.Count -ne 1) { throw 'Expected one dedicated NX main window.' }
$nxMainHandle = [long]$nxMain[0].Current.NativeWindowHandle
switch ($Action) {
    'InspectUI' {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $owned = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
        $all = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,$owned)
        $rows = @(foreach ($item in $all) {
            if ($item.Current.Name -or $item.Current.AutomationId) {
                [ordered]@{name=$item.Current.Name; id=$item.Current.AutomationId; type=$item.Current.ControlType.ProgrammaticName;
                    handle=$item.Current.NativeWindowHandle; enabled=$item.Current.IsEnabled; rect=[string]$item.Current.BoundingRectangle}
            }
        })
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($rows | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
        $rows | ConvertTo-Json -Depth 4
    }
    { $_ -in @('OpenPart','Load') } {
        $part = (Resolve-Path -LiteralPath $launch.copied_part).Path
        $dialogName = [string][char]0x6253 + [char]0x5F00
        if ($Action -eq 'Load') {
            $part = (Resolve-Path -LiteralPath $ImagePath).Path
            $build = [IO.Path]::GetFullPath($launch.build_root).TrimEnd('\') + '\'
            if (-not $part.StartsWith($build,[StringComparison]::OrdinalIgnoreCase)) { throw 'Only project build images may be loaded.' }
            $dialogName = -join @(0x6267,0x884C,0x7528,0x6237,0x51FD,0x6570 | ForEach-Object { [char]$_ })
        }
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $owned = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $dialogCondition = [System.Windows.Automation.AndCondition]::new($owned,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$dialogName))
        $dialog = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$dialogCondition)
        if ($null -eq $dialog) {
            if ($Action -eq 'Load') {
                $loadTimer = [Diagnostics.Stopwatch]::StartNew()
                do {
                    Start-Sleep -Milliseconds 100
                    $dialog = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$dialogCondition)
                } while ($null -eq $dialog -and $loadTimer.ElapsedMilliseconds -lt 5000)
                if ($null -eq $dialog) { throw 'Verified NX Open dialog not found.' }
            }
            else {
            & (Join-Path $scripts 'click-nx-window.ps1') -NxPid $process.Id -X 78 -Y 100
            $openTimer = [Diagnostics.Stopwatch]::StartNew()
            do {
                $dialog = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$dialogCondition)
                if ($null -eq $dialog) { Start-Sleep -Milliseconds 100 }
            } while ($null -eq $dialog -and $openTimer.ElapsedMilliseconds -lt 5000)
            }
        }
        if ($null -eq $dialog) { throw 'Verified NX file-open dialog not found.' }
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class NxM2Filename {
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,string l);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,StringBuilder l);
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h,int id);
 [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
 public static string ClassName(IntPtr h){var b=new StringBuilder(128);GetClassName(h,b,b.Capacity);return b.ToString();}
 public static string Text(IntPtr h){var b=new StringBuilder(4096);SendMessage(h,0xD,(IntPtr)b.Capacity,b);return b.ToString();}
 public static void SetText(IntPtr h,string s){SendMessage(h,0xC,IntPtr.Zero,s);}
 public static void Click(IntPtr h){SendMessage(h,0xF5,IntPtr.Zero,IntPtr.Zero);}
 public static void Submit(IntPtr dialog,IntPtr button){SendMessage(dialog,0x111,(IntPtr)1,button);}
 public static void MarkEdited(IntPtr edit){SendMessage(edit,0xB1,(IntPtr)(-1),(IntPtr)(-1));SendMessage(edit,0x102,(IntPtr)'x',IntPtr.Zero);SendMessage(edit,0x102,(IntPtr)8,IntPtr.Zero);SendMessage(edit,0xB9,(IntPtr)1,IntPtr.Zero);IntPtr combo=GetParent(edit),dialog=GetParent(combo);SendMessage(combo,0x111,(IntPtr)((0x300<<16)|1148),edit);SendMessage(dialog,0x111,(IntPtr)((5<<16)|1148),combo);}
}
'@
        $candidates = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'1148'))
        $edits = @(foreach ($item in $candidates) {
            if ([NxM2Filename]::ClassName([IntPtr]$item.Current.NativeWindowHandle) -eq 'Edit') { $item }
        })
        if ($edits.Count -ne 1) { throw 'Expected one verified native filename edit.' }
        $edit = $edits[0]
        [NxM2Filename]::SetText([IntPtr]$edit.Current.NativeWindowHandle,$part)
        if ($Action -eq 'Load') { [NxM2Filename]::MarkEdited([IntPtr]$edit.Current.NativeWindowHandle) }
        $value = [NxM2Filename]::Text([IntPtr]$edit.Current.NativeWindowHandle)
        if ($value -ne $part) { throw 'File-open path readback differs; no open submitted.' }
        $buttonHandle = [NxM2Filename]::GetDlgItem([IntPtr]$dialog.Current.NativeWindowHandle,1)
        if ($buttonHandle -eq [IntPtr]::Zero -or [NxM2Filename]::Text($buttonHandle) -ne ([string][char]0x786E + [char]0x5B9A)) {
            throw 'Verified native open button missing.'
        }
        [NxM2Filename]::Submit([IntPtr]$dialog.Current.NativeWindowHandle,$buttonHandle)
        Start-Sleep -Milliseconds 1000
        & (Join-Path $scripts 'nx-ui.ps1') -NxPid $process.Id -Action Capture -OutputPath $OutputPath
    }
    'Click' {
        & (Join-Path $scripts 'click-nx-window.ps1') -NxPid $process.Id -WindowHandle $nxMainHandle -X $X -Y $Y -NoActivate
        & (Join-Path $scripts 'nx-ui.ps1') -NxPid $process.Id -Action Capture -OutputPath $OutputPath
    }
    'Capture' {
        & (Join-Path $scripts 'nx-ui.ps1') -NxPid $process.Id -Action Capture -OutputPath $OutputPath | Out-Null
        [uint32]$windowPid = 0
        $windowThread = [NxM2Dpi]::GetWindowThreadProcessId([IntPtr]$nxMainHandle,[ref]$windowPid)
        if ($windowPid -ne $process.Id) { throw 'Main window process identity differs.' }
        $capture = Get-Content -Raw -Encoding UTF8 -LiteralPath $OutputPath | ConvertFrom-Json
        $capture | Add-Member -NotePropertyName nx_window_thread_id -NotePropertyValue ([string]$windowThread)
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($capture | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
        $capture | ConvertTo-Json -Depth 6
    }
    'OpenLoad' {
        & (Join-Path $scripts 'nx-ui.ps1') -NxPid $process.Id -Action LoadDialog -OutputPath $OutputPath
    }
    'OpenLoadMenu' {
        & (Join-Path $scripts 'click-nx-window.ps1') -NxPid $process.Id -WindowHandle $nxMainHandle -X 50 -Y 57 -NoActivate
        & (Join-Path $scripts 'click-nx-window.ps1') -NxPid $process.Id -WindowHandle $nxMainHandle -X 75 -Y 485 -NoActivate
        & (Join-Path $scripts 'click-nx-window.ps1') -NxPid $process.Id -WindowHandle $nxMainHandle -X 320 -Y 240 -NoActivate
        & (Join-Path $scripts 'nx-ui.ps1') -NxPid $process.Id -Action Capture -OutputPath $OutputPath
    }
    'Screenshot' {
        & (Join-Path $scripts 'capture-nx-window.ps1') -NxPid $process.Id -WindowHandle $nxMainHandle -OutputPath $OutputPath -Visible
    }
    'Close' {
        & (Join-Path $PSScriptRoot 'close-m2-test-nx.ps1') -LaunchReport $LaunchReport -OutputPath $OutputPath -SavedPartPath $SavedPartPath
    }
}
