# Offline compile only. This does not launch NX or run the acceptance entry point.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$build = Join-Path $projectRoot 'workspace/build/current/M2'
$compiler = 'C:/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$nxManaged = 'C:/0sf/ug/NXBIN/managed'
$arguments = @('/nologo','/langversion:5','/target:library','/platform:x64',('/out:' + (Join-Path $build 'M7Acceptance.dll')))
foreach ($reference in @((Join-Path $build 'M2Bridge.dll'),(Join-Path $build 'M2Entry.dll'),
    (Join-Path $nxManaged 'NXOpen.dll'),(Join-Path $nxManaged 'NXOpen.UF.dll'),(Join-Path $nxManaged 'NXOpen.Utilities.dll'))) {
    if(-not (Test-Path -LiteralPath $reference)) { throw "Missing reference: $reference" }
    $arguments += '/reference:' + $reference
}
$arguments += Join-Path $PSScriptRoot 'M7Acceptance.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'M7 acceptance compilation failed' }
