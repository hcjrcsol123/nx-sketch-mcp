# Read-only effective firewall and optional dedicated NX listener capture.
param(
    [string]$OutputPath = '',
    [string]$NxExe = 'C:\0sf\ug\NXBIN\ugraf.exe',
    [int]$NxPid = 0
)
$ErrorActionPreference = 'Stop'
if ($OutputPath -and (Test-Path -LiteralPath $OutputPath)) { throw 'Refusing to overwrite network evidence.' }
$nxPath = (Resolve-Path -LiteralPath $NxExe).Path
$profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore)
$rule = Get-NetFirewallRule -PolicyStore ActiveStore -Name 'nx-sketch-mcp-local-only-ugraf'
$app = $rule | Get-NetFirewallApplicationFilter
$port = $rule | Get-NetFirewallPortFilter
$address = $rule | Get-NetFirewallAddressFilter
$enabled = @($profiles | Where-Object { [string]$_.Enabled -ne 'True' }).Count -eq 0
$matches = [string]$rule.Enabled -eq 'True' -and [string]$rule.Direction -eq 'Inbound' -and
    [string]$rule.Action -eq 'Block' -and [string]$rule.Profile -eq 'Any' -and
    [string]$app.Program -eq $nxPath -and [string]$port.Protocol -eq 'TCP' -and
    [string]$port.LocalPort -eq 'Any' -and [string]$address.RemoteAddress -eq 'Any'
$listeners = @()
if ($NxPid -gt 0) {
    $process = Get-Process -Id $NxPid
    if ($process.ProcessName -ne 'ugraf' -or $process.Path -ne $nxPath) { throw 'Unexpected dedicated NX process.' }
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $NxPid } |
        Select-Object LocalAddress,LocalPort,OwningProcess)
}
$report = [ordered]@{
    captured_utc = [DateTime]::UtcNow.ToString('o')
    profiles = @($profiles | ForEach-Object { @{name=[string]$_.Name; enabled=[string]$_.Enabled} })
    rule = @{name=[string]$rule.Name; enabled=[string]$rule.Enabled; action=[string]$rule.Action;
        direction=[string]$rule.Direction; profile=[string]$rule.Profile; program=[string]$app.Program;
        protocol=[string]$port.Protocol; local_port=[string]$port.LocalPort; remote_address=[string]$address.RemoteAddress}
    inbound_isolation_configured = ($enabled -and $matches)
    nx_pid = $NxPid
    listeners = $listeners
    changed_firewall = $false
    note = 'Effective Windows firewall configuration captured; no external reachability scan was performed.'
}
if ($OutputPath) {
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), ($report | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
}
$report | ConvertTo-Json -Depth 7
if (-not $report.inbound_isolation_configured) { throw 'Required NX inbound isolation is not configured; do not launch NX.' }
