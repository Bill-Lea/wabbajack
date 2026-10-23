$ErrorActionPreference = "Stop"

# ============================================================
# Custom Wabbajack build + safe local deployment
# ============================================================

$Source = "C:\CodePackages\Wabbajack_clone"
$Root   = "F:\Game Utilities\Wabbajack"

# Do NOT overwrite the official 4.2.3.0 installation.
# Assembly version MUST contain only numbers.
# Base is now upstream 4.3.2.1 (Avalonia app).
$BuildVersion = "4.3.2.221"

# Directory name can identify this as our custom build.
$CustomFolder = "4.3.2.221"

$TargetApp = Join-Path $Root $CustomFolder
$PublishRoot     = Join-Path $Source "publish-custom"
$PublishApp      = Join-Path $PublishRoot "app"
$PublishLauncher = Join-Path $PublishRoot "launcher"

# Upstream removed the WPF app in 4.3.x; the app is now Avalonia (net10.0).
$AppProject      = Join-Path $Source "Wabbajack.App.Avalonia\Wabbajack.App.Avalonia.csproj"
$LauncherProject = Join-Path $Source "Wabbajack.Launcher\Wabbajack.Launcher.csproj"
$CliProject      = Join-Path $Source "Wabbajack.CLI\Wabbajack.CLI.csproj"

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " CUSTOM WABBAJACK BUILD" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

Write-Host "Source : $Source"
Write-Host "Root   : $Root"
Write-Host "App    : $TargetApp"
Write-Host ""

# ------------------------------------------------------------
# Verify paths
# ------------------------------------------------------------

foreach ($path in @($Source, $Root, $AppProject, $LauncherProject, $CliProject)) {
    if (-not (Test-Path $path)) {
        throw "Required path not found: $path"
    }
}

Write-Host "Using .NET:" -ForegroundColor Yellow
dotnet --version

# ------------------------------------------------------------
# Stop Wabbajack
# ------------------------------------------------------------

Write-Host ""
Write-Host "Stopping Wabbajack..." -ForegroundColor Yellow

Get-Process Wabbajack -ErrorAction SilentlyContinue |
    Stop-Process -Force

Start-Sleep -Seconds 2

# ------------------------------------------------------------
# Remove ONLY our temporary build output
# ------------------------------------------------------------

if (Test-Path $PublishRoot) {
    Write-Host "Removing previous custom publish directory..."
    Remove-Item $PublishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $PublishApp -Force | Out-Null
New-Item -ItemType Directory -Path $PublishLauncher -Force | Out-Null

Set-Location $Source

# ------------------------------------------------------------
# Clear stale build data
# ------------------------------------------------------------

Write-Host ""
Write-Host "Removing stale bin/obj directories..." -ForegroundColor Yellow

Get-ChildItem $Source -Directory -Recurse -Force |
    Where-Object { $_.Name -in @("bin","obj") } |
    Sort-Object FullName -Descending |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# ------------------------------------------------------------
# Restore
# ------------------------------------------------------------

Write-Host ""
Write-Host "Restoring solution for win-x64..." -ForegroundColor Yellow

dotnet restore ".\Wabbajack.sln" --runtime win-x64

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed."
}

# ------------------------------------------------------------
# Common official release arguments
# ------------------------------------------------------------

$CommonArgs = @(
    "--framework", "net10.0-windows",
    "--runtime", "win-x64",
    "--configuration", "Release",
    "--self-contained",
    "--no-restore",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:DebugType=embedded",
"/p:VERSION=$BuildVersion"
)

# ------------------------------------------------------------
# Publish main application
# ------------------------------------------------------------

# The Avalonia app targets plain net10.0; the launcher and CLI are net10.0-windows.
# (Mirrors the official release.ps1 publish recipe.)
$AppArgs = @(
    "--framework", "net10.0",
    "--runtime", "win-x64",
    "--configuration", "Release",
    "--self-contained",
    "--no-restore",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:DebugType=embedded",
    "/p:VERSION=$BuildVersion"
)

Write-Host ""
Write-Host "Publishing Wabbajack application (Avalonia)..." -ForegroundColor Yellow

dotnet publish $AppProject @AppArgs --output $PublishApp

if ($LASTEXITCODE -ne 0) {
    throw "Wabbajack.App.Wpf publish failed."
}

# ------------------------------------------------------------
# Publish CLI into app\cli
# ------------------------------------------------------------

Write-Host ""
Write-Host "Publishing Wabbajack CLI..." -ForegroundColor Yellow

$CliTarget = Join-Path $PublishApp "cli"

dotnet publish $CliProject @CommonArgs --output $CliTarget

if ($LASTEXITCODE -ne 0) {
    throw "Wabbajack.CLI publish failed."
}

# ------------------------------------------------------------
# Publish standalone launcher
# ------------------------------------------------------------

Write-Host ""
Write-Host "Publishing standalone launcher..." -ForegroundColor Yellow

dotnet publish $LauncherProject @CommonArgs `
    --output $PublishLauncher `
    /p:PublishSingleFile=true

if ($LASTEXITCODE -ne 0) {
    throw "Wabbajack.Launcher publish failed."
}

# ------------------------------------------------------------
# Verify builds
# ------------------------------------------------------------

$BuiltApp      = Join-Path $PublishApp "Wabbajack.exe"
$BuiltLauncher = Join-Path $PublishLauncher "Wabbajack.exe"

if (-not (Test-Path $BuiltApp)) {
    throw "Published application Wabbajack.exe not found."
}

if (-not (Test-Path $BuiltLauncher)) {
    throw "Published launcher Wabbajack.exe not found."
}

Write-Host ""
Write-Host "Build succeeded." -ForegroundColor Green

Get-Item $BuiltApp,$BuiltLauncher |
    Select-Object FullName,Length,
        @{N="FileVersion";E={$_.VersionInfo.FileVersion}}

# ------------------------------------------------------------
# Protect existing modlists
# ------------------------------------------------------------

$ExistingLists = Join-Path $Root "4.2.3.0\downloaded_mod_lists"

if (-not (Test-Path $ExistingLists)) {
    throw "SAFETY STOP: Existing downloaded_mod_lists was not found at $ExistingLists"
}

$listCount = @(
    Get-ChildItem $ExistingLists -Filter "*.wabbajack" -File `
        -ErrorAction SilentlyContinue
).Count

Write-Host ""
Write-Host "Existing downloaded modlists: $listCount" -ForegroundColor Green
Write-Host "Source: $ExistingLists"

# ------------------------------------------------------------
# Install custom application
# ------------------------------------------------------------

Write-Host ""
Write-Host "Installing custom application..." -ForegroundColor Yellow

if (Test-Path $TargetApp) {
    Remove-Item $TargetApp -Recurse -Force
}

New-Item -ItemType Directory -Path $TargetApp -Force | Out-Null

Copy-Item "$PublishApp\*" $TargetApp -Recurse -Force

# Copy the modlist store into the custom version.
# We COPY it. We do NOT move or delete the original.
$CustomLists = Join-Path $TargetApp "downloaded_mod_lists"

Write-Host "Copying downloaded_mod_lists..." -ForegroundColor Yellow

Copy-Item $ExistingLists $CustomLists -Recurse -Force

# ------------------------------------------------------------
# Install launcher
# ------------------------------------------------------------

$RootLauncher = Join-Path $Root "Wabbajack.exe"
$LauncherBackup = Join-Path $Root "Wabbajack.exe.pre-custom-backup"

Write-Host ""
Write-Host "Backing up current launcher..." -ForegroundColor Yellow

if (Test-Path $RootLauncher) {
    Copy-Item $RootLauncher $LauncherBackup -Force
}

Write-Host "Installing new standalone launcher..." -ForegroundColor Yellow

Copy-Item $BuiltLauncher $RootLauncher -Force

# ------------------------------------------------------------
# Final verification
# ------------------------------------------------------------

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host " CUSTOM INSTALL COMPLETE" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green

Write-Host ""
Write-Host "Launcher:"
Write-Host "  $RootLauncher"

Write-Host ""
Write-Host "Custom application:"
Write-Host "  $TargetApp"

Write-Host ""
Write-Host "Original application preserved:"
Write-Host "  $Root\4.2.3.0"

Write-Host ""
Write-Host "Launcher backup:"
Write-Host "  $LauncherBackup"

Write-Host ""
Write-Host "Downloaded modlists copied:"
Write-Host "  $listCount"

Write-Host ""
Write-Host "DO NOT delete the 4.2.3.0 directory yet." -ForegroundColor Yellow