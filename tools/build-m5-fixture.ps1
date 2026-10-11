# Offline build for the standalone native NXOpen M5 fixture entry.
# Output: workspace/build/current/M2/M5Fixture.dll. Does not start or load NX.
$ErrorActionPreference = 'Stop'

$nxProcesses = @(Get-Process -Name 'ugraf' -ErrorAction SilentlyContinue)
if ($nxProcesses.Count -gt 0) { throw 'Close NX (ugraf.exe) before building M5Fixture.dll.' }

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = Join-Path $projectRoot 'tests\nx\M5FixtureEntry.cs'
$outputDir = Join-Path $projectRoot 'workspace\build\current\M2'
$output = Join-Path $outputDir 'M5Fixture.dll'
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
if (-not (Test-Path -LiteralPath $source)) { throw "M5 fixture source missing: $source" }

$nxBase = $env:UGII_BASE_DIR
if ([string]::IsNullOrWhiteSpace($nxBase)) { throw 'UGII_BASE_DIR is required to build M5Fixture.dll.' }
$managed = Join-Path $nxBase 'NXBIN\managed'
$references = @(
    (Join-Path $frameworkDir 'System.dll'),
    (Join-Path $frameworkDir 'System.Core.dll'),
    (Join-Path $frameworkDir 'System.Web.Extensions.dll'),
    (Join-Path $managed 'NXOpen.dll'),
    (Join-Path $managed 'NXOpen.Utilities.dll'),
    (Join-Path $managed 'NXOpen.UF.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) { throw "Required reference missing: $reference" }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$arguments = @('/nologo', '/langversion:5', '/target:library', '/platform:x64', '/optimize+', '/deterministic', "/out:$output")
foreach ($reference in $references) { $arguments += "/reference:$reference" }
$arguments += $source
Write-Output 'compile M5Fixture.dll'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "M5Fixture.dll compile failed (exit $LASTEXITCODE)" }

$item = Get-Item -LiteralPath $output
[pscustomobject]@{ path = $item.FullName; bytes = $item.Length }
