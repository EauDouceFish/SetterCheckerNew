param([Parameter(Mandatory)][string]$Output)

# Builds the folder that goes to SVN for the pipeline: scripts, manual baseline and a self-contained tool in bin/.
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot 'Src/SetterChecker.Cli/SetterChecker.Cli.csproj') -c Release -r win-x64 --self-contained `
    -p:SatelliteResourceLanguages=zh-Hans -o (Join-Path $Output 'bin')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -LiteralPath @('Run-Pipeline.ps1', 'Run-Khengine.ps1', 'khengine-manual-baseline.json' | ForEach-Object { Join-Path $PSScriptRoot $_ }) -Destination $Output
Write-Host "Pipeline package: $Output"
