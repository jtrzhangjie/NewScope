$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$generated = @(
    "src\NewScope\bin", "src\NewScope\obj",
    "tests\NewScope.Tests\bin", "tests\NewScope.Tests\obj", "tests\NewScope.Tests\TestResults",
    "tools\NewScope.Stress\bin", "tools\NewScope.Stress\obj",
    "TestResults", ".vs"
)
foreach ($relative in $generated) {
    $path = Join-Path $root $relative
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
        Write-Host "Removed $relative"
    }
}
Write-Host "Build intermediates cleaned. Final programs and archives under dist/ are retained." -ForegroundColor Green
