# Creates a new saved independent copy and an identifiable dedicated NX process.
param(
    [Parameter(Mandatory=$true)][string]$EvidenceDir,
    [Parameter(Mandatory=$true)][string]$NetworkReport,
    [string]$SourcePart = '',
    [string]$PythonExe = '',
    [ValidateSet('M2-3-M2-4','M2-5-M2-6','M2-7','M3','M4','M5','M6')][string]$TestStage = 'M2-3-M2-4'
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourcePart)) {
    $SourcePart = Join-Path $PSScriptRoot '..\workspace\parts\USER_TEST_PART\model1.prt'
}
$evidence = (Resolve-Path -LiteralPath $EvidenceDir).Path
$launchPath = Join-Path $evidence 'launch.json'
if (Test-Path -LiteralPath $launchPath) { throw 'Refusing to overwrite launch evidence.' }
if (Get-Process ugraf -ErrorAction SilentlyContinue) { throw 'NX is already running; dedicated session required.' }
$network = Get-Content -Raw -Encoding UTF8 -LiteralPath $NetworkReport | ConvertFrom-Json
if ($network.inbound_isolation_configured -ne $true) { throw 'NX inbound isolation was not confirmed.' }
$networkCapturedUtc = if ($network.captured_utc -is [DateTime]) { $network.captured_utc.ToUniversalTime() }
    else { [DateTimeOffset]::Parse([string]$network.captured_utc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime }
if (([DateTime]::UtcNow - $networkCapturedUtc).TotalMinutes -gt 30) {
    throw 'Repeat the effective network check before launch.'
}
$runId = [Guid]::NewGuid().ToString('N')
$partDir = Join-Path $PSScriptRoot ('..\workspace\parts\' + $TestStage + '-' + [DateTime]::Now.ToString('yyyyMMdd') + '\' + $runId)
$partDir = [IO.Path]::GetFullPath($partDir)
New-Item -ItemType Directory -Path $partDir | Out-Null
$source = (Resolve-Path -LiteralPath $SourcePart).Path
$copyName = if ($TestStage -eq 'M6') { 'm6-input.prt' }
    elseif ($TestStage -eq 'M5') { 'm5-input.prt' }
    elseif ($TestStage -eq 'M4') { 'm4-input.prt' }
    elseif ($TestStage -eq 'M3') { 'm3-input.prt' }
    elseif ($TestStage -eq 'M2-7') { 'm2-e2e-input.prt' }
    elseif ($TestStage -eq 'M2-5-M2-6') { 'm2-parameter-copy.prt' } else { 'm2-read-copy.prt' }
$copy = Join-Path $partDir $copyName
[IO.File]::Copy($source, $copy, $false)
$settings = . (Join-Path $PSScriptRoot 'prepare-m2-session.ps1') -PythonExe $PythonExe
$env:NX_SKETCH_MCP_M2_TRANSPORT_REPORT = Join-Path $evidence 'nx-channels.json'
$exe = Join-Path $env:UGII_BASE_DIR 'NXBIN\ugraf.exe'
$process = Start-Process -FilePath $exe -WorkingDirectory $evidence -WindowStyle Hidden -PassThru
$record = [ordered]@{
    launched_utc=[DateTime]::UtcNow.ToString('o'); pid=$process.Id;
    process_start_utc=$process.StartTime.ToUniversalTime().ToString('o'); exe=$exe;
    source_part=$source; copied_part=$copy; copy_bytes=(Get-Item -LiteralPath $copy).Length;
    config_path=$settings.ConfigPath; client_config_path=$settings.ClientConfigPath;
    build_root=$settings.BuildRoot; network_report=[IO.Path]::GetFullPath($NetworkReport); test_stage=$TestStage
}
[IO.File]::WriteAllText($launchPath, ($record | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$record | ConvertTo-Json -Depth 5
