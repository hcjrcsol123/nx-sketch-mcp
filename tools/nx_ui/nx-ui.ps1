# GUI operations scoped to one NX PID. No NXOpen calls; records observed windows.
param(
  [Parameter(Mandatory=$true)][int]$NxPid,
  [Parameter(Mandatory=$true)][ValidateSet('Capture','LoadDialog','EnterImage','OpenModal','Cancel','Close')][string]$Action,
  [Parameter(Mandatory=$true)][string]$OutputPath,
  [string]$ImagePath
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputPath){throw 'Refusing to overwrite UI evidence'}
if(-not(Test-Path -LiteralPath (Split-Path -Parent $OutputPath))){throw 'Evidence parent missing'}
$process=Get-Process -Id $NxPid
if($process.ProcessName -ne 'ugraf'){throw 'Target is not ugraf'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NxTestWindow {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
}
'@
if($Action -ne 'Capture'){
  $shell=New-Object -ComObject WScript.Shell
  if(-not $shell.AppActivate($NxPid)){throw 'Cannot activate dedicated NX'}
  Start-Sleep -Milliseconds 300
  [uint32]$frontPid=0
  [void][NxTestWindow]::GetWindowThreadProcessId([NxTestWindow]::GetForegroundWindow(),[ref]$frontPid)
  if($frontPid -ne $NxPid){throw 'Foreground PID mismatch; no input sent'}
  switch($Action){
    'LoadDialog' {$shell.SendKeys('^u')}
    'OpenModal' {$shell.SendKeys('^o')}
    'Cancel' {$shell.SendKeys('{ESC}')}
    'Close' {if(-not $process.CloseMainWindow()){throw 'Normal close request failed'}}
    'EnterImage' {
      $full=(Resolve-Path -LiteralPath $ImagePath).Path
      if($full -match '[+^%~(){}\[\]]'){throw 'Path contains SendKeys metacharacters'}
      # Separate action allows capture/inspection of the actual Ctrl-U dialog before entering path.
      $shell.SendKeys($full)
      $shell.SendKeys('{ENTER}')
    }
  }
  Start-Sleep -Milliseconds 700
}
$root=[System.Windows.Automation.AutomationElement]::RootElement
$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$NxPid)
$items=$root.FindAll([System.Windows.Automation.TreeScope]::Children,$condition)
$windows=@()
foreach($item in $items){
  $modal=$null
  try {$pattern=$item.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern);$modal=$pattern.Current.IsModal} catch {}
  $windows+=[ordered]@{name=$item.Current.Name;enabled=$item.Current.IsEnabled;offscreen=$item.Current.IsOffscreen;is_modal=$modal;automation_id=$item.Current.AutomationId}
}
$result=[ordered]@{captured_utc=[DateTime]::UtcNow.ToString('o');pid=$NxPid;process_start_utc=$process.StartTime.ToUniversalTime().ToString('o');action=$Action;windows=$windows;note='Keystroke delivery is not proof of image load or modality; verify server log and disabled owner window.'}
[IO.File]::WriteAllText($OutputPath,($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 5
