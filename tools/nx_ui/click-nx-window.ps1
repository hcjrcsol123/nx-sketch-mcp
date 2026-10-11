param([Parameter(Mandatory=$true)][int]$NxPid,[Parameter(Mandatory=$true)][int]$X,[Parameter(Mandatory=$true)][int]$Y,[long]$WindowHandle=0,[switch]$NoActivate)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NxScopedClick {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h,bool altTab);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int cmd);
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool attach);
 [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
 [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 public static void Activate(IntPtr h){uint p;var front=GetWindowThreadProcessId(GetForegroundWindow(),out p);var target=GetWindowThreadProcessId(h,out p);var current=GetCurrentThreadId();bool attached=current!=front&&AttachThreadInput(current,front,true);bool targetAttached=current!=target&&target!=front&&AttachThreadInput(current,target,true);try{ShowWindow(h,3);BringWindowToTop(h);SwitchToThisWindow(h,true);SetForegroundWindow(h);SetFocus(h);}finally{if(targetAttached)AttachThreadInput(current,target,false);if(attached)AttachThreadInput(current,front,false);}}
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint data,UIntPtr extra);
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
}
'@
$p=Get-Process -Id $NxPid
if($p.ProcessName -ne 'ugraf'){throw 'Expected dedicated NX'}
$w=New-Object -ComObject WScript.Shell
$targetHandle=$p.MainWindowHandle
if($WindowHandle){$targetHandle=[IntPtr]$WindowHandle}
if(-not $NoActivate){[NxScopedClick]::Activate($targetHandle)}

Start-Sleep -Milliseconds 300
[uint32]$front=0
[void][NxScopedClick]::GetWindowThreadProcessId([NxScopedClick]::GetForegroundWindow(),[ref]$front)
if($front -ne $NxPid){Start-Sleep -Milliseconds 300;[NxScopedClick]::Activate($targetHandle);[void][NxScopedClick]::GetWindowThreadProcessId([NxScopedClick]::GetForegroundWindow(),[ref]$front)}
if($front -ne $NxPid){throw ('Foreground identity differs: '+$front)}
$rect=New-Object NxScopedClick+Rect
$target=$p.MainWindowHandle
if($WindowHandle){$target=[IntPtr]$WindowHandle}
[uint32]$owner=0
[void][NxScopedClick]::GetWindowThreadProcessId($target,[ref]$owner)
if($owner -ne $NxPid){throw 'Target window belongs to another process'}
[void][NxScopedClick]::GetWindowRect($target,[ref]$rect)
if($X -lt 0 -or $Y -lt 0 -or $X -ge ($rect.Right-$rect.Left) -or $Y -ge ($rect.Bottom-$rect.Top)){throw 'Point outside NX'}
[void][NxScopedClick]::SetCursorPos($rect.Left+$X,$rect.Top+$Y)
[NxScopedClick]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
[NxScopedClick]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
