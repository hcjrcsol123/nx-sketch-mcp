# Offline build for the M2/M3 CLR-only IPC bridge, JSONL helper, and NX entry assembly.
# Output: workspace/build/current/M2/{M2Bridge.dll,M2JsonlHelper.exe,M2Entry.dll,M2Startup.dll}
# This script compiles only. It does not start NX or load an NX image.
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceRoot = Join-Path $projectRoot 'src\bridge'
$outputDir = Join-Path $projectRoot 'workspace\build\current\M2'
$frameworkDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$compilerCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    (Join-Path $frameworkDir 'csc.exe')
)
$compiler = $null
foreach ($candidate in $compilerCandidates) {
    if (Test-Path -LiteralPath $candidate) { $compiler = $candidate; break }
}
if (-not $compiler) { throw 'No offline C# compiler found (Roslyn or .NET Framework csc).' }

$frameworkRefs = @(
    'System.dll',
    'System.Core.dll',
    'System.Security.dll',
    'System.Runtime.Remoting.dll',
    'System.Windows.Forms.dll',
    'System.Drawing.dll'
)
$refs = @()
foreach ($name in $frameworkRefs) {
    $path = Join-Path $frameworkDir $name
    if (-not (Test-Path -LiteralPath $path)) { throw "required Framework reference missing: $name" }
    $refs += $path
}

$bridgeSource = Join-Path $sourceRoot 'M2Bridge.cs'
$contractSource = Join-Path $sourceRoot 'M2ModelContract.cs'
$m3ContractSource = Join-Path $sourceRoot 'M3ModelContract.cs'
$helperSource = Join-Path $sourceRoot 'M2JsonlHelper.cs'
$entrySource = Join-Path $sourceRoot 'M2Entry.cs'
$adapterSource = Join-Path $sourceRoot 'M2OpenModelAdapter.cs'
$m3FactorySource = Join-Path $sourceRoot 'M3NxFeatureFactory.cs'
$m7FactorySource = Join-Path $sourceRoot 'M7NxHelicalFactory.cs'
$targetSource = Join-Path $sourceRoot 'M2FrameworkTarget.cs'
$startupSource = Join-Path $sourceRoot 'M2Startup.cs'
foreach ($source in @($bridgeSource, $contractSource, $m3ContractSource, $helperSource, $entrySource, $adapterSource, $m3FactorySource, $m7FactorySource, $targetSource, $startupSource)) {
    if (-not (Test-Path -LiteralPath $source)) { throw "source file missing: $source" }
}

$nxBase = $env:UGII_BASE_DIR
if ([string]::IsNullOrWhiteSpace($nxBase)) { throw 'UGII_BASE_DIR is required to build M2Entry.dll.' }
$nxOpen = Join-Path $nxBase 'NXBIN\managed\NXOpen.dll'
$nxUf = Join-Path $nxBase 'NXBIN\managed\NXOpen.UF.dll'
$nxUtilities = Join-Path $nxBase 'NXBIN\managed\NXOpen.Utilities.dll'
$nxUi = Join-Path $nxBase 'NXBIN\managed\NXOpenUI.dll'
foreach ($reference in @($nxOpen, $nxUf, $nxUtilities, $nxUi)) {
    if (-not (Test-Path -LiteralPath $reference)) { throw "NXOpen reference missing under UGII_BASE_DIR: $reference" }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$bridgeDll = Join-Path $outputDir 'M2Bridge.dll'
$helperExe = Join-Path $outputDir 'M2JsonlHelper.exe'
$entryDll = Join-Path $outputDir 'M2Entry.dll'
$startupDll = Join-Path $outputDir 'M2Startup.dll'

function Invoke-M2Csc([string]$label, [string]$target, [string]$output, [string[]]$references, [string[]]$sources) {
    $arguments = @('/nologo', '/langversion:5', "/target:$target", '/platform:x64', '/optimize+', '/deterministic', "/out:$output")
    foreach ($reference in $references) { $arguments += "/reference:$reference" }
    $arguments += $sources
    Write-Output ("compile " + $label)
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $label (exit $LASTEXITCODE)" }
}

$bridgeRefs = @($refs)
Invoke-M2Csc 'M2Bridge.dll' 'library' $bridgeDll $bridgeRefs @($bridgeSource, $contractSource, $m3ContractSource, $targetSource)
Invoke-M2Csc 'M2JsonlHelper.exe' 'exe' $helperExe (@($refs) + @($bridgeDll)) @($helperSource, $targetSource)
Invoke-M2Csc 'M2Entry.dll' 'library' $entryDll (@($refs) + @($bridgeDll, $nxOpen, $nxUf, $nxUtilities, $nxUi)) @($entrySource, $adapterSource, $m3FactorySource, $m7FactorySource, $targetSource)
Invoke-M2Csc 'M2Startup.dll' 'library' $startupDll (@($refs) + @($nxOpen)) @($startupSource, $targetSource)

Write-Output ''
Write-Output 'build complete'
foreach ($artifact in @($bridgeDll, $helperExe, $entryDll, $startupDll)) {
    $item = Get-Item -LiteralPath $artifact
    [pscustomobject]@{ path = $item.FullName; bytes = $item.Length }
}
