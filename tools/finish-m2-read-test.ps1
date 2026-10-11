# Cleanup only the credential of the recorded dedicated process after normal exit.
param(
    [Parameter(Mandatory=$true)][string]$LaunchReport,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { throw 'Refusing to overwrite cleanup evidence.' }
$launch = Get-Content -Raw -Encoding UTF8 -LiteralPath $LaunchReport | ConvertFrom-Json
$config = Get-Content -Raw -Encoding UTF8 -LiteralPath $launch.config_path | ConvertFrom-Json
if (Get-Process -Id $launch.pid -ErrorAction SilentlyContinue) { throw 'Recorded NX PID is still present; no credential deleted.' }
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$runtime = Join-Path $project 'workspace\runtime\M2\sessions'
$configPath = [IO.Path]::GetFullPath($launch.config_path)
$tokenPath = [IO.Path]::GetFullPath($config.token_file)
$expectedToken = Join-Path (Split-Path -Parent $configPath) 'private\bridge.token'
if (-not $configPath.StartsWith($runtime + '\',[StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($tokenPath,$expectedToken,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Credential path is outside this recorded project session.'
}
$existed = Test-Path -LiteralPath $tokenPath -PathType Leaf
if ($existed) { Remove-Item -LiteralPath $tokenPath }
$listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $launch.pid })
$report = [ordered]@{
    captured_utc=[DateTime]::UtcNow.ToString('o'); nx_pid=$launch.pid; nx_process_exited=$true;
    token_existed_after_nx_exit=$existed; token_removed=(-not (Test-Path -LiteralPath $tokenPath));
    nx_tcp_listener_count=$listeners.Count; force_terminated=$false; changed_firewall=$false
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($report | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$report | ConvertTo-Json
