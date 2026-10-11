param(
    [Parameter(Mandatory = $true)]
    [string] $Config
)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'mcp_stdio.py'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "MCP launcher was not found: $launcher"
}
$configPath = (Resolve-Path -LiteralPath $Config).Path

py -3.14 -B $launcher --config $configPath
exit $LASTEXITCODE
