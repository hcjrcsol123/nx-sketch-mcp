param(
    [string]$EvidenceDir
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $runDate = [DateTime]::Now.ToString('yyyyMMdd')
    $runStamp = [DateTime]::Now.ToString('yyyyMMddTHHmmssfff')
    $suiteRoot = Join-Path $projectRoot (Join-Path ('evidence\M3-' + $runDate) 'software-suite')
    $EvidenceDir = Join-Path $suiteRoot ('run-' + $runStamp)
}
$evidenceDir = if ([IO.Path]::IsPathRooted($EvidenceDir)) {
    [IO.Path]::GetFullPath($EvidenceDir)
} else {
    [IO.Path]::GetFullPath((Join-Path $projectRoot $EvidenceDir))
}
if (Test-Path -LiteralPath $evidenceDir) { throw "EvidenceDir already exists; refusing to overwrite: $evidenceDir" }
$stdoutPath = Join-Path $evidenceDir 'unittest-stdout.txt'
$stderrPath = Join-Path $evidenceDir 'unittest-stderr.txt'
$summaryPath = Join-Path $evidenceDir 'suite-summary.json'
$detailsPath = Join-Path $evidenceDir 'software-suite.jsonl'
$compilerPath = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$pythonCommand = Get-Command -Name 'python.exe' -CommandType Application -ErrorAction Stop | Select-Object -First 1
$pythonExe = $pythonCommand.Source
$pythonVersion = (& $pythonExe --version 2>&1 | Out-String).Trim()
$compilerVersion = if (Test-Path -LiteralPath $compilerPath -PathType Leaf) { (& $compilerPath /version 2>&1 | Out-String).Trim() } else { 'not found' }

New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null
$startedUtc = [DateTime]::UtcNow.ToString('o')
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONUTF8 = '1'
$env:M2_PUBLIC_EVIDENCE_PATH = $detailsPath
$processInfo = [Diagnostics.ProcessStartInfo]::new()
$processInfo.FileName = $pythonExe
$processInfo.Arguments = '-B -m unittest discover -s tests/m2 -p test_*.py -v'
$processInfo.WorkingDirectory = $projectRoot
$processInfo.UseShellExecute = $false
$processInfo.CreateNoWindow = $true
$processInfo.RedirectStandardOutput = $true
$processInfo.RedirectStandardError = $true
$processInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$processInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
$testProcess = [Diagnostics.Process]::new()
$testProcess.StartInfo = $processInfo
if (-not $testProcess.Start()) { throw 'Could not start the Python unittest process.' }
$stdoutTask = $testProcess.StandardOutput.ReadToEndAsync()
$stderrTask = $testProcess.StandardError.ReadToEndAsync()
$testProcess.WaitForExit()
$testExitCode = $testProcess.ExitCode
$finishedUtc = [DateTime]::UtcNow.ToString('o')
$stdoutText = $stdoutTask.GetAwaiter().GetResult()
$stderrText = $stderrTask.GetAwaiter().GetResult()
$testProcess.Dispose()
[IO.File]::WriteAllText($stdoutPath, $stdoutText, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($stderrPath, $stderrText, [Text.UTF8Encoding]::new($false))
$testText = $stdoutText + [Environment]::NewLine + $stderrText

$ranMatch = [regex]::Match($testText, 'Ran\s+(\d+)\s+tests?\s+in\s+([0-9.]+)s')
$testsRun = $null
$elapsedSeconds = $null
if ($ranMatch.Success) {
    $testsRun = [int]$ranMatch.Groups[1].Value
    $elapsedSeconds = [double]$ranMatch.Groups[2].Value
}
$resultLabel = if ($testExitCode -eq 0) { 'PASS' } else { 'FAIL' }
$summary = [ordered]@{
    started_utc = $startedUtc
    finished_utc = $finishedUtc
    python_executable = $pythonExe
    python_version = $pythonVersion
    csharp_compiler = $compilerPath
    csharp_compiler_version = $compilerVersion
    build_outputs = @(
        (Join-Path $projectRoot 'workspace\build\current\M2\M2Bridge.dll'),
        (Join-Path $projectRoot 'workspace\build\current\M2\M2JsonlHelper.exe'),
        (Join-Path $projectRoot 'workspace\build\current\M2\M2Entry.dll')
    )
    tests_run = $testsRun
    unittest_elapsed_seconds = $elapsedSeconds
    exit_code = $testExitCode
    result = $resultLabel
    stdout_file = $stdoutPath
    stderr_file = $stderrPath
    evidence_dir = $evidenceDir
    detailed_runtime_evidence = $detailsPath
}
$summaryJson = $summary | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($summaryPath, $summaryJson, [Text.UTF8Encoding]::new($false))
Write-Output $testText
Write-Output ('evidence_summary=' + $summaryPath)
exit $testExitCode
