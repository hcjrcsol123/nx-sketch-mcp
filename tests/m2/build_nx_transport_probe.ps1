param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\workspace\build\current\M2\tests'),
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')][string]$ImageName = 'NxTransportProbeEntry'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$frameworkDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$compilerCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    (Join-Path $frameworkDir 'csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $compiler) { throw 'No offline C# compiler found (Roslyn or .NET Framework csc).' }

$nxBase = $env:UGII_BASE_DIR
if ([string]::IsNullOrWhiteSpace($nxBase)) { throw 'UGII_BASE_DIR is required to build the NX-loadable transport probe.' }
$nxOpen = Join-Path $nxBase 'NXBIN\managed\NXOpen.dll'
$source = Join-Path $PSScriptRoot 'NxTransportProbeEntry.cs'
$targetSource = Join-Path $projectRoot 'src\bridge\M2FrameworkTarget.cs'
foreach ($path in @($nxOpen, $source, $targetSource)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required transport-probe input missing: $path" }
}

$frameworkNames = @('System.dll', 'System.Core.dll', 'System.Runtime.Remoting.dll', 'System.Web.Extensions.dll')
$references = @()
foreach ($name in $frameworkNames) {
    $path = Join-Path $frameworkDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Framework reference missing: $path" }
    $references += $path
}
$references += $nxOpen

$outputDir = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$output = Join-Path $outputDir ($ImageName + '.dll')
$arguments = @('/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+', ('/out:' + $output))
foreach ($reference in $references) { $arguments += '/reference:' + $reference }
$arguments += @($source, $targetSource)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Compile failed: NxTransportProbeEntry.dll (exit $LASTEXITCODE)" }

$item = Get-Item -LiteralPath $output
[pscustomobject]@{ path = $item.FullName; bytes = $item.Length; compiler = $compiler; nxopen_reference = $nxOpen }
