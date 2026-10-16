param(
    [ValidateSet('EditMode', 'PlayMode')][string]$Mode = 'EditMode',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.0.62f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'My project'
$results = Join-Path $repo 'TestResults'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$arguments = @('-batchmode', '-nographics', '-projectPath', ('"' + $project + '"'),
    '-runTests', '-testPlatform', $Mode, '-testFilter', 'PuppyPi.Tests',
    '-testResults', ('"' + (Join-Path $results ($Mode + '.xml')) + '"'),
    '-logFile', ('"' + (Join-Path $results ($Mode + '.log')) + '"'))
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
Write-Output ($Mode + ' Unity exit code: ' + $process.ExitCode)
if ((Test-Path -LiteralPath (Join-Path $results ($Mode + '.xml'))) -and
    (Get-Item -LiteralPath (Join-Path $results ($Mode + '.xml'))).LastWriteTimeUtc -ge $started) {
    [xml]$report = Get-Content -LiteralPath (Join-Path $results ($Mode + '.xml'))
    $report.'test-run' | Select-Object result,total,passed,failed,skipped,duration
}
elseif ($process.ExitCode -eq 0) { throw 'Unity did not produce a fresh test result XML. See the log.' }
exit $process.ExitCode
