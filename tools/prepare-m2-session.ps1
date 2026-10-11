param(
    [string]$BuildRoot = (Join-Path $PSScriptRoot '..\workspace\build\current\M2'),
    [string]$SessionRoot = (Join-Path $PSScriptRoot '..\workspace\runtime\M2\sessions'),
    [string]$PythonExe = ''
)

$ErrorActionPreference = 'Stop'

# Dot-source this script in the PowerShell session that will launch NX so the two
# environment variables are inherited by NX. The MCP client reads the JSON config
# returned by this script; it does not need those environment variables.
$sessionId = [Guid]::NewGuid().ToString('N')
$pipeName = 'nxsketchmcp_m2_' + $sessionId
$sessionRootPath = [IO.Path]::GetFullPath($SessionRoot)
$buildRootPath = [IO.Path]::GetFullPath($BuildRoot)
$sessionDir = Join-Path $sessionRootPath $sessionId
$privateDir = Join-Path $sessionDir 'private'
$tokenFile = Join-Path $privateDir 'bridge.token'
$configPath = Join-Path $sessionDir 'mcp-config.json'
$clientConfigPath = Join-Path $sessionDir 'mcp-servers.json'
$helperPath = Join-Path $buildRootPath 'M2JsonlHelper.exe'
$launcherPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'mcp_stdio.py'))

if ([string]::IsNullOrWhiteSpace($PythonExe)) {
    $pythonCommand = Get-Command -Name 'python.exe' -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $PythonExe = $pythonCommand.Source
}
$pythonPath = [IO.Path]::GetFullPath($PythonExe)

foreach ($requiredPath in @($pythonPath, $launcherPath, $helperPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required M2 runtime file is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Path $privateDir -Force | Out-Null

# Protect the directory so the token that M2Entry creates there inherits a
# current-user-only ACL. This script creates the path, not the secret itself.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$privateDirectoryInfo = [IO.DirectoryInfo]::new($privateDir)
$acl = $privateDirectoryInfo.GetAccessControl()
$acl.SetAccessRuleProtection($true, $false)
$inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
$rule = [Security.AccessControl.FileSystemAccessRule]::new(
    $identity.User,
    [Security.AccessControl.FileSystemRights]::FullControl,
    $inheritance,
    [Security.AccessControl.PropagationFlags]::None,
    [Security.AccessControl.AccessControlType]::Allow
)
$acl.SetOwner($identity.User)
$acl.AddAccessRule($rule)
$privateDirectoryInfo.SetAccessControl($acl)

$config = [ordered]@{
    pipe_name = $pipeName
    token_file = [IO.Path]::GetFullPath($tokenFile)
    helper_path = $helperPath
    timeout_seconds = 5.0
}
$json = $config | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($configPath, $json, [Text.UTF8Encoding]::new($false))

$clientConfig = [ordered]@{
    mcpServers = [ordered]@{
        'nx-sketch-mcp' = [ordered]@{
            command = $pythonPath
            args = @('-B', $launcherPath, '--config', [IO.Path]::GetFullPath($configPath))
        }
    }
}
$clientJson = $clientConfig | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($clientConfigPath, $clientJson, [Text.UTF8Encoding]::new($false))

$env:NX_SKETCH_MCP_M2_PIPE = $pipeName
$env:NX_SKETCH_MCP_M2_TOKEN_FILE = [IO.Path]::GetFullPath($tokenFile)

[pscustomobject]@{
    PipeName = $pipeName
    TokenFile = [IO.Path]::GetFullPath($tokenFile)
    ConfigPath = [IO.Path]::GetFullPath($configPath)
    ClientConfigPath = [IO.Path]::GetFullPath($clientConfigPath)
    PythonExe = $pythonPath
    HelperPath = $helperPath
    BuildRoot = $buildRootPath
}
