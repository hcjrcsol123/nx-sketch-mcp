# Update the already configured local installation. No NX/process termination or config/token changes.
param([string]$InstallRoot = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $codexHome = $env:CODEX_HOME
    if ([string]::IsNullOrWhiteSpace($codexHome)) {
        $codexHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
    }
    $InstallRoot = Join-Path $codexHome 'nx-sketch-mcp'
}
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$install = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\','/')
$metadataPath = Join-Path $install 'installation.json'
if (-not (Test-Path -LiteralPath $metadataPath)) { throw 'Existing installation.json is required; this tool does not register a new MCP installation.' }
$metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([IO.Path]::GetFullPath([string]$metadata.source_root).TrimEnd('\','/') -ne $projectRoot) { throw 'Installation source_root differs from this source tree.' }
if (Get-Process -Name ugraf,M2JsonlHelper -ErrorAction SilentlyContinue) {
    throw 'NX or a bridge helper is running. Save and close the appropriate session before replacing loaded assemblies; no process was changed.'
}
$app = Join-Path $install 'app'
$files = [Collections.Generic.List[object]]::new()
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/nx_sketch_mcp') -Filter '*.py' -File) {
    $files.Add(@{ source=$source.FullName; target=(Join-Path $app ('src/nx_sketch_mcp/' + $source.Name)) })
}
foreach ($name in @('M2Bridge.dll','M2Entry.dll','M2JsonlHelper.exe','M2Startup.dll')) {
    $files.Add(@{ source=(Join-Path $projectRoot ('workspace/build/current/M2/' + $name)); target=(Join-Path $app ('workspace/build/current/M2/' + $name)) })
}
$files.Add(@{ source=(Join-Path $projectRoot 'workspace/build/current/M2/M2Startup.dll'); target=(Join-Path $app 'nx-profile/startup/M2Startup.dll') })
$files.Add(@{ source=(Join-Path $projectRoot 'README.md'); target=(Join-Path $app 'README.md') })
$files.Add(@{ source=(Join-Path $projectRoot 'docs/M7-native-helical.md'); target=(Join-Path $app 'docs/M7-native-helical.md') })
$files.Add(@{ source=(Join-Path $projectRoot 'docs/installed-README.md'); target=(Join-Path $install 'README.md') })
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.source -PathType Leaf)) { throw ('Missing source: ' + $file.source) }
    $target = [IO.Path]::GetFullPath($file.target)
    if (-not $target.StartsWith($app + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($target, (Join-Path $install 'README.md'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installation target escaped allowed app/README paths.'
    }
}
$evidence = [Collections.Generic.List[object]]::new()
foreach ($file in $files) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file.target) | Out-Null
    Copy-Item -LiteralPath $file.source -Destination $file.target -Force
    $sourceHash = (Get-FileHash -LiteralPath $file.source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $file.target -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) { throw 'Installed file hash differs from source.' }
    $evidence.Add(@{ file=$file.target; sha256=$targetHash })
}
$metadata | Add-Member -NotePropertyName version -NotePropertyValue '0.3.0' -Force
$metadata | Add-Member -NotePropertyName updated_utc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataPath -Encoding utf8
@{ version='0.3.0'; files=$evidence; reconnect_mcp_required=$true; existing_nx_hot_reloaded=$false } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $projectRoot 'workspace/build/current/M2/installed-m7.json') -Encoding utf8
Write-Output ('Installed version 0.3.0; ' + $files.Count + ' files matched SHA256. Reconnect MCP to discover 20 tools.')
