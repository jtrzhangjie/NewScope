param(
    [int[]]$Channels = @(32, 128, 256, 512),
    [ValidateRange(1, 86400)][int]$DurationSeconds = 300,
    [ValidateRange(1, 10000)][int]$Frames = 120,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot "NewScope.Stress\NewScope.Stress.csproj"
if (-not $OutputDirectory) {
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
    $OutputDirectory = Join-Path $root "dist\stress\$stamp"
}
if ($Channels.Count -eq 0 -or @($Channels | Where-Object { $_ -lt 1 -or $_ -gt 4096 }).Count -gt 0) {
    throw "Channels must contain values between 1 and 4096."
}
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

dotnet build $project -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Stress harness build failed." }

$history = Join-Path $OutputDirectory "history.json"
dotnet run --project $project -c Release --no-build -- `
    --root $root --channels ($Channels -join ',') --duration 0 --frames $Frames --output $history
if ($LASTEXITCODE -ne 0) { throw "History/render stress failed. See $history" }

$liveChannels = if ($Channels -contains 256) { 256 } else { ($Channels | Measure-Object -Maximum).Maximum }
$live = Join-Path $OutputDirectory "live.json"
dotnet run --project $project -c Release --no-build -- `
    --root $root --channels $liveChannels --duration $DurationSeconds --ui-only --output $live
if ($LASTEXITCODE -ne 0) { throw "Live UI stress failed. See $live" }

Write-Host "Stress reports: $OutputDirectory" -ForegroundColor Green
