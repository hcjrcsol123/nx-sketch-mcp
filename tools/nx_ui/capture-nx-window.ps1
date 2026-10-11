param([Parameter(Mandatory=$true)][int]$NxPid,[Parameter(Mandatory=$true)][string]$OutputPath,[long]$WindowHandle=0,[switch]$Visible)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputPath){throw 'Refusing existing screenshot'}
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NxWindowCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
}
'@
$process=Get-Process -Id $NxPid
if($process.ProcessName -ne 'ugraf'){throw 'Expected dedicated NX'}
$handle=$process.MainWindowHandle
if($WindowHandle){$handle=[IntPtr]$WindowHandle}
[uint32]$owner=0
[void][NxWindowCapture]::GetWindowThreadProcessId($handle,[ref]$owner)
if($owner -ne $NxPid){throw 'Window PID mismatch'}
$rect=New-Object NxWindowCapture+Rect
if(-not [NxWindowCapture]::GetWindowRect($handle,[ref]$rect)){throw 'No NX window'}
$bitmap=New-Object Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
$graphics=[Drawing.Graphics]::FromImage($bitmap)
if($Visible){$graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size);$ok=$true}
else {$dc=$graphics.GetHdc();try {$ok=[NxWindowCapture]::PrintWindow($handle,$dc,2)} finally {$graphics.ReleaseHdc($dc)}}
try {if(-not $ok){throw 'PrintWindow failed'}; $bitmap.Save([IO.Path]::GetFullPath($OutputPath),[Drawing.Imaging.ImageFormat]::Png)} finally {$graphics.Dispose();$bitmap.Dispose()}
