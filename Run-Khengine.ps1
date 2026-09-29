param([int]$Jobs = 8, [string]$Output = "$env:TEMP/SetterChecker-khengine", [string]$ProjectRoot = 'D:/KiHan', [string]$Previous, [switch]$ApplyNlt)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build Src/SetterChecker.Cli/SetterChecker.Cli.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $baseline = Join-Path $PSScriptRoot 'khengine-manual-baseline.json'
    $arguments = @('analyze',
        '--project', "$ProjectRoot/Packages/khengine/Runtime/khengine.runtime.asmdef",
        '--project', "$ProjectRoot/Packages/khengine/define/khengine.define.asmdef",
        "-j$Jobs", '--reflection-baseline', '--output', $Output)
    if (Test-Path -LiteralPath $baseline) { $arguments += @('--baseline', $baseline) }
    if ($Previous) { $arguments += @('--previous', $Previous) }
    if ($ApplyNlt) { $arguments += '--apply-nlt' }
    dotnet Src/SetterChecker.Cli/bin/Release/net10.0/SetterChecker.Cli.dll @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
