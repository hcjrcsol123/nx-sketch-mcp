param(
    [Parameter(Mandatory = $true)][string]$BuildRoot,
    [Parameter(Mandatory = $true)][string]$SessionRoot,
    [Parameter(Mandatory = $true)][string]$PythonExe
)

$ErrorActionPreference = 'Stop'
$prepareScript = Join-Path $PSScriptRoot '..\..\tools\prepare-m2-session.ps1'
$prepared = & $prepareScript -BuildRoot $BuildRoot -SessionRoot $SessionRoot -PythonExe $PythonExe
$runtimeConfig = Get-Content -LiteralPath $prepared.ConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
$clientConfig = Get-Content -LiteralPath $prepared.ClientConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
$tokenExists = Test-Path -LiteralPath $prepared.TokenFile

[pscustomobject]@{
    prepared = $prepared
    environment = [ordered]@{
        pipe = $env:NX_SKETCH_MCP_M2_PIPE
        token_file = $env:NX_SKETCH_MCP_M2_TOKEN_FILE
    }
    runtime_config = $runtimeConfig
    client_config = $clientConfig
    token_file_exists_before_nx_load = $tokenExists
} | ConvertTo-Json -Depth 10 -Compress
