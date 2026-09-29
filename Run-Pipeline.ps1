param(
    [string]$ProjectRoot = 'D:/KiHan',
    [string]$StateDir = "$env:TEMP/SetterChecker-pipeline-state",
    [string]$Output = "$env:TEMP/SetterChecker-pipeline",
    [int]$Jobs = 8,
    [switch]$SvnUpdate,
    [string]$UnityPath,
    [switch]$FailOnNotify
)

# Exit codes: 0 done (see notify.md), 3 new missing labels with -FailOnNotify, others are svn/Unity/build/analysis failures.
$ErrorActionPreference = 'Stop'
if ($SvnUpdate) {
    svn update $ProjectRoot --non-interactive
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
New-Item -ItemType Directory -Force $Output | Out-Null
if ($UnityPath) {
    $unityLog = Join-Path $Output 'unity.log'
    $unity = Start-Process -FilePath $UnityPath -Wait -PassThru -NoNewWindow `
        -ArgumentList @('-batchmode', '-quit', '-projectPath', $ProjectRoot, '-logFile', $unityLog)
    if ($unity.ExitCode -ne 0) { Write-Host "Unity script compilation failed, see $unityLog"; exit $unity.ExitCode }
}
dotnet restore (Join-Path $PSScriptRoot 'SetterChecker.slnx')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$previous = Join-Path $StateDir 'functions.json'
$hasPrevious = Test-Path -LiteralPath $previous
$notifyPath = Join-Path $Output 'notify.json'
Remove-Item -LiteralPath $notifyPath -ErrorAction SilentlyContinue
$analysis = @{ Jobs = $Jobs; Output = $Output; ProjectRoot = $ProjectRoot }
if ($hasPrevious) { $analysis.Previous = $previous }
& (Join-Path $PSScriptRoot 'Run-Khengine.ps1') @analysis
$code = $LASTEXITCODE
# 1 only means some functions are still undetermined; the reports are complete.
if ($code -notin 0, 1 -or -not (Test-Path -LiteralPath $notifyPath)) { Write-Host "SetterChecker failed with exit code $code"; exit $(if ($code -eq 0) { 2 } else { $code }) }

$notify = Get-Content -LiteralPath $notifyPath -Raw -Encoding UTF8 | ConvertFrom-Json
New-Item -ItemType Directory -Force $StateDir | Out-Null
Copy-Item -LiteralPath (Join-Path $Output 'functions.json') -Destination $previous -Force
if (-not $hasPrevious) {
    Write-Host "First run: comparison baseline saved to $previous, no notification this time."
    exit 0
}
Write-Host "New functions judged NoLogTrack but missing the label: $($notify.Count). Details: $(Join-Path $Output 'notify.md')"
if ($FailOnNotify -and $notify.Count -gt 0) { exit 3 }
exit 0
