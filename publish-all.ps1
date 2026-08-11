# QrintPrint One-Click Publish Script
# Generates four release versions and packs them as zip

$ErrorActionPreference = "Stop"

# Config
$ProjectPath = "src\QrintPrint"
$ProjectFile = "$ProjectPath\QrintPrint.csproj"
$DistDir = "dist"
$ReleaseDir = "release"
$Version = "v1.0.0"
$Timestamp = Get-Date -Format "yyyyMMdd"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  QrintPrint Publish Script" -ForegroundColor Cyan
Write-Host "  Version: $Version ($Timestamp)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Clean old dist
if (Test-Path $DistDir) { Remove-Item $DistDir -Recurse -Force }
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null

# Version 1: Framework-dependent single-file EXE (needs .NET 8, only one EXE)
Write-Host "`n[1/4] Building framework-dependent single-file EXE..." -ForegroundColor Yellow
dotnet publish $ProjectFile -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$DistDir\1-framework-dependent-single"
Write-Host "      Done" -ForegroundColor Green

# Version 2: Framework-dependent multi-file (needs .NET 8, smallest total size)
Write-Host "`n[2/4] Building framework-dependent multi-file version..." -ForegroundColor Yellow
dotnet publish $ProjectFile -c Release -r win-x64 --self-contained false -o "$DistDir\2-framework-dependent-multi"
Write-Host "      Done" -ForegroundColor Green

# Version 3: Self-contained multi-file (no .NET install needed)
Write-Host "`n[3/4] Building self-contained multi-file version..." -ForegroundColor Yellow
dotnet publish $ProjectFile -c Release -r win-x64 --self-contained true -o "$DistDir\3-self-contained-multi"
Write-Host "      Done" -ForegroundColor Green

# Version 4: Self-contained single-file (single EXE, no .NET install needed)
Write-Host "`n[4/4] Building self-contained single-file version..." -ForegroundColor Yellow
dotnet publish $ProjectFile -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$DistDir\4-self-contained-single"
Write-Host "      Done" -ForegroundColor Green

# Pack as zip
Write-Host "`nPacking zip files..." -ForegroundColor Yellow

$pkgs = @(
    @{ Dir = "1-framework-dependent-single"; Name = "framework-dependent-single" },
    @{ Dir = "2-framework-dependent-multi"; Name = "framework-dependent-multi" },
    @{ Dir = "3-self-contained-multi"; Name = "self-contained-multi" },
    @{ Dir = "4-self-contained-single"; Name = "self-contained-single" }
)

foreach ($pkg in $pkgs) {
    $zipPath = "$ReleaseDir\QrintPrint-$Version-$Timestamp-$($pkg.Name).zip"
    Compress-Archive -Path "$DistDir\$($pkg.Dir)\*" -DestinationPath $zipPath -Force
    $size = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "   OK: $($pkg.Name) -> $zipPath ($size MB)" -ForegroundColor Green
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  Publish Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "`nOutput: $ReleaseDir\"
Write-Host ""
Write-Host "Versions:" -ForegroundColor White
Write-Host "  1. Framework-dependent single-file - ~3MB, needs .NET 8, only one EXE"
Write-Host "  2. Framework-dependent multi-file  - ~3MB, needs .NET 8, multiple DLLs"
Write-Host "  3. Self-contained multi-file       - ~72MB, unzip and run"
Write-Host "  4. Self-contained single-file      - ~66MB, single EXE, no install needed"
