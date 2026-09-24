param([int]$Jobs = 8, [string]$Output = "$env:TEMP/SetterChecker-khengine")

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build Src/SetterChecker.Cli/SetterChecker.Cli.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $baseline = Join-Path $PSScriptRoot 'khengine-manual-baseline.json'
    $baselineArgument = if (Test-Path -LiteralPath $baseline) { @('--baseline', $baseline) } else { @() }
    dotnet Src/SetterChecker.Cli/bin/Release/net10.0/SetterChecker.Cli.dll analyze --project D:/KiHan/Packages/khengine/Runtime/khengine.runtime.asmdef "-j$Jobs" --reflection-baseline @baselineArgument --output $Output
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
