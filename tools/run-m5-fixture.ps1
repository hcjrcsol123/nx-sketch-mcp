# Explicit native bench actions only; run after MCP returns HUMAN_OWNED.
param(
    [Parameter(Mandatory=$true)][string]$LaunchReport,
    [Parameter(Mandatory=$true)][string]$ConfigPath,
    [Parameter(Mandatory=$true)][string]$PartDirectory,
    [Parameter(Mandatory=$true)][string]$ReportPath,
    [Parameter(Mandatory=$true)][ValidateSet('create','no_history','open','saved_dirty','unsaved','readonly','inspect','assembly','close_test_parts','select_part','external_reference')][string]$Mode,
    [string]$PartPath = '',
    [string]$FixtureImageName = 'M5Fixture.dll',
    [switch]$KeyboardLoad
)
$ErrorActionPreference = 'Stop'
$launch = Get-Content -Raw -Encoding UTF8 -LiteralPath $LaunchReport | ConvertFrom-Json
$report = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $report) { throw 'Use an absent fixture report path.' }
$config = [ordered]@{output_dir=[IO.Path]::GetFullPath($PartDirectory); mode=$Mode; report_path=$report}
if ($PartPath) { $config.part_path = [IO.Path]::GetFullPath($PartPath) }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($ConfigPath), ($config | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$ui = Join-Path $PSScriptRoot 'm2-nx-test-ui.ps1'
$nxActivation = New-Object -ComObject WScript.Shell
if (-not $nxActivation.AppActivate([int]$launch.pid)) { throw 'Cannot activate the recorded bench NX.' }
Start-Sleep -Milliseconds 300
$loadAction = if ($KeyboardLoad) { 'OpenLoad' } else { 'OpenLoadMenu' }
& $ui -LaunchReport $LaunchReport -Action $loadAction -OutputPath ($report + '.menu.json') | Out-Null
if ([IO.Path]::GetFileName($FixtureImageName) -ne $FixtureImageName) { throw 'FixtureImageName must be a file name within the build directory.' }
& $ui -LaunchReport $LaunchReport -Action Load -ImagePath (Join-Path $launch.build_root $FixtureImageName) -OutputPath ($report + '.load.json') | Out-Null
$timer = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath $report) -and $timer.ElapsedMilliseconds -lt 15000) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path -LiteralPath $report)) { throw 'NX fixture has not written its report; inspect NX UI.' }
$result = Get-Content -Raw -Encoding UTF8 -LiteralPath $report | ConvertFrom-Json
[pscustomobject]@{status=$result.status; mode=$result.mode; current_part=$result.current_part; report_path=$report} | ConvertTo-Json -Depth 5
if ($result.status -ne 'OK') { throw ('Native fixture failed; see ' + $report) }
