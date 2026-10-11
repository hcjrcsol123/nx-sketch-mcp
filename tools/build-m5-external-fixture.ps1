# Offline build for the separate native NX interpart-reference fixture.
# This emits only the new M5ExternalReference.dll and never touches M5Fixture/M2 artifacts.
param([string]$OutputFileName = 'M5ExternalReference.dll')
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = Join-Path $projectRoot 'tests\nx\M5ExternalReferenceEntry.cs'
$outputDir = Join-Path $projectRoot 'workspace\build\current\M2'
$safeName = [System.IO.Path]::GetFileName($OutputFileName)
if ($safeName -ne $OutputFileName -or [System.IO.Path]::GetExtension($safeName) -ne '.dll') {
    throw 'OutputFileName must be a simple .dll file name.'
}
if ($safeName -in @('M5Fixture.dll', 'M2Entry.dll', 'M2Bridge.dll', 'M2JsonlHelper.exe')) {
    throw "OutputFileName is reserved for a fixture or production M2 assembly: $safeName"
}
$output = Join-Path $outputDir $safeName
if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite existing independent fixture DLL: $output" }

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
if (-not (Test-Path -LiteralPath $source)) { throw "External reference source missing: $source" }

$nxBase = $env:UGII_BASE_DIR
if ([string]::IsNullOrWhiteSpace($nxBase)) { throw 'UGII_BASE_DIR is required to build M5ExternalReference.dll.' }
$managed = Join-Path $nxBase 'NXBIN\managed'
$references = @(
    (Join-Path $frameworkDir 'System.dll'),
    (Join-Path $frameworkDir 'System.Core.dll'),
    (Join-Path $frameworkDir 'System.Web.Extensions.dll'),
    (Join-Path $managed 'NXOpen.dll'),
    (Join-Path $managed 'NXOpen.Utilities.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) { throw "Required reference missing: $reference" }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$arguments = @('/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+', '/deterministic', "/out:$output")
foreach ($reference in $references) { $arguments += "/reference:$reference" }
$arguments += $source
Write-Output ("compile " + $safeName)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "$safeName compile failed (exit $LASTEXITCODE)" }

$item = Get-Item -LiteralPath $output
[pscustomobject]@{ path = $item.FullName; bytes = $item.Length }
