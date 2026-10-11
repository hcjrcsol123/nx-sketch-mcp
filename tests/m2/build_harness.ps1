param(
    [Parameter(Mandatory = $true)][string]$OutputExe,
    [Parameter(Mandatory = $true)][string]$BridgeAssembly
)

$ErrorActionPreference = 'Stop'
$compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$frameworkDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$source = Join-Path $PSScriptRoot 'M2StaHarness.cs'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler not found: $compiler"
}
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Harness source not found: $source"
}
if (-not (Test-Path -LiteralPath $BridgeAssembly -PathType Leaf)) {
    throw "M2 bridge assembly not found: $BridgeAssembly"
}
$references = @('System.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll') |
    ForEach-Object { Join-Path $frameworkDir $_ }
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) {
        throw "Framework reference not found: $reference"
    }
}

$outputDirectory = Split-Path -Parent $OutputExe
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$arguments = @('/nologo', '/langversion:5', '/target:exe', '/platform:x64', ('/out:' + $OutputExe))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += '/reference:' + (Resolve-Path -LiteralPath $BridgeAssembly).Path
$arguments += $source
& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Harness compilation failed with exit code $LASTEXITCODE"
}
Copy-Item -LiteralPath $BridgeAssembly -Destination (Join-Path $outputDirectory 'M2Bridge.dll') -Force
Write-Output $OutputExe
