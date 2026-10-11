# Run in an administrator PowerShell only when local-only NX use is intended.
# Adds one restrictive inbound TCP rule; no port is opened and no existing rule
# is changed. MCP stdio, local Windows IPC and NX outbound connections remain.
param(
    [string]$NxExe = (Join-Path $env:UGII_BASE_DIR 'NXBIN\ugraf.exe')
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This narrow firewall rule requires an administrator PowerShell. No firewall change was made.'
}
if (-not (Test-Path -LiteralPath $NxExe -PathType Leaf)) {
    throw 'The supplied installed NX executable does not exist.'
}
$nxPath = (Resolve-Path -LiteralPath $NxExe).Path
if ([IO.Path]::GetFileName($nxPath) -ne 'ugraf.exe') {
    throw 'This script only supports the installed ugraf.exe program.'
}

$ruleName = 'nx-sketch-mcp-local-only-ugraf'
$rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
if ($null -ne $rule) {
    $application = $rule | Get-NetFirewallApplicationFilter
    $port = $rule | Get-NetFirewallPortFilter
    if ($rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Block' -or
        $rule.Enabled -ne 'True' -or $rule.Profile -ne 'Any' -or
        $application.Program -ne $nxPath -or $port.Protocol -ne 'TCP') {
        throw 'A different rule already uses the task rule name; it was not changed.'
    }
} else {
    $rule = New-NetFirewallRule -Name $ruleName -DisplayName 'nx-sketch-mcp: NX local-only inbound TCP' `
        -Description 'Block remote inbound TCP to NX for local-only use; nx-sketch-mcp uses stdio and Windows IPC.' `
        -Program $nxPath -Protocol TCP -Direction Inbound -Action Block -Enabled True -Profile Any
}

[pscustomobject]@{
    RuleName = $ruleName
    Program = $nxPath
    Direction = [string]$rule.Direction
    Action = [string]$rule.Action
    Profile = [string]$rule.Profile
    Enabled = [string]$rule.Enabled
    RemoveCommand = 'Remove-NetFirewallRule -Name nx-sketch-mcp-local-only-ugraf'
} | ConvertTo-Json
