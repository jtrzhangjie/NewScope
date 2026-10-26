param(
    [switch]$SkipTests,
    [switch]$SkipPackage,
    [string]$NsisCompiler
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "NewScope.slnx"
$project = Join-Path $root "src\NewScope\NewScope.csproj"
$testProject = Join-Path $root "tests\NewScope.Tests\NewScope.Tests.csproj"
$stressProject = Join-Path $root "tools\NewScope.Stress\NewScope.Stress.csproj"
$nsi = Join-Path $root "packaging\NewScope.nsi"
$publishDir = Join-Path $root "dist\publish"
$symbolsDir = Join-Path $root "dist\symbols"
$portableArchive = Join-Path $root "dist\NewScope-Portable-win-x64.zip"
$installerPath = Join-Path $root "dist\NewScope-Setup-win-x64.exe"
$checksumPath = Join-Path $root "dist\SHA256SUMS.txt"

$metadata = dotnet msbuild $project -nologo "-getProperty:Version,FileVersion" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
    throw "Could not read project version metadata."
}
$version = $metadata.Properties.Version
$fileVersion = $metadata.Properties.FileVersion
$manifest = [xml](Get-Content (Join-Path $root "src\NewScope\app.manifest") -Raw)
if ($manifest.assembly.assemblyIdentity.version -ne $fileVersion) {
    throw "app.manifest version must match FileVersion $fileVersion."
}

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

Invoke-Step "Restore" { dotnet restore $solution -v q --nologo }
Invoke-Step "Build" { dotnet build $project -c Release -v q --nologo --no-restore }
Invoke-Step "Stress harness build" { dotnet build $stressProject -c Release -v q --nologo --no-restore }

if (-not $SkipTests) {
    Invoke-Step "Tests" {
        dotnet test $testProject -c Release -v q --nologo --no-restore
    }
}

Invoke-Step "Publish" {
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=portable `
        -p:DebugSymbols=true `
        -o $publishDir `
        -v q --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed with exit code $LASTEXITCODE"
    }

    $pdbs = @(Get-ChildItem $publishDir -Filter *.pdb -File -Recurse)
    if ($pdbs.Count -eq 0) {
        throw "Publish did not produce debug symbols."
    }
    New-Item $symbolsDir -ItemType Directory -Force | Out-Null
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
    $symbolArchive = Join-Path $symbolsDir "NewScope-$version-win-x64-$stamp-symbols.zip"
    Compress-Archive -Path $pdbs.FullName -DestinationPath $symbolArchive -Force
    $pdbs | Remove-Item -Force
    Write-Host "Symbols: $symbolArchive" -ForegroundColor Green

    Copy-Item (Join-Path $root "README.md") $publishDir -Force
    Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") $publishDir -Force
    Copy-Item (Join-Path $root "packaging\NSIS.COPYING.txt") $publishDir -Force
}

Write-Host "==> Portable ZIP" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $portableArchive -Force

if (-not $SkipPackage) {
    if (-not $NsisCompiler) {
        $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
        if ($command) {
            $NsisCompiler = $command.Source
        } else {
            $candidates = @(
                (Join-Path $env:ProgramFiles "NSIS\makensis.exe"),
                (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe"),
                (Join-Path $env:LOCALAPPDATA "Programs\NSIS\makensis.exe"),
                (Join-Path $env:TEMP "opencode\nsis\nsis-3.11\makensis.exe")
            )
            $NsisCompiler = $candidates |
                Where-Object { $_ -and (Test-Path $_) } |
                Select-Object -First 1
        }
    }

    if (-not $NsisCompiler -or -not (Test-Path $NsisCompiler)) {
        throw "NSIS compiler not found. Install NSIS 3.x or pass -NsisCompiler <path to makensis.exe>."
    }

    Invoke-Step "Installer" {
        & $NsisCompiler "-DAPP_VERSION=$version" "-DAPP_FILE_VERSION=$fileVersion" -INPUTCHARSET UTF8 -V2 $nsi
    }
    $setup = Get-Item $installerPath -ErrorAction SilentlyContinue
    if ($null -eq $setup) {
        throw "Installer was not produced."
    }

    Write-Host ("Installer: {0} ({1:N0} bytes)" -f $setup.FullName, $setup.Length) -ForegroundColor Green
    Write-Host ("SHA256: {0}" -f (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
}

$artifacts = @($portableArchive)
if (-not $SkipPackage) { $artifacts += $installerPath }
$latestSymbols = Get-ChildItem $symbolsDir -Filter "NewScope-$version-win-x64-*-symbols.zip" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($latestSymbols) { $artifacts += $latestSymbols.FullName }
$lines = foreach ($artifact in $artifacts) {
    "{0}  {1}" -f (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $artifact -Leaf)
}
[System.IO.File]::WriteAllLines($checksumPath, [string[]]$lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Portable: $portableArchive" -ForegroundColor Green
Write-Host "Checksums: $checksumPath" -ForegroundColor Green
