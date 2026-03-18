param(
    [string]$GamePath,
    [string]$PackageDir = $PSScriptRoot,
    [switch]$AutoCloseGame
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "InstallerCommon.ps1")

Assert-GameNotRunning -AutoClose:$AutoCloseGame

$resolvedGamePath = Resolve-Sts2GamePath -PreferredPath $GamePath
if (-not $resolvedGamePath) {
    throw "Could not auto-detect game path. Please pass it manually: Install-Sts2Mcp.bat ""D:\SteamLibrary\steamapps\common\Slay the Spire 2"""
}

$dllSource = Join-Path $PackageDir "Sts2Mcp.dll"
$pckSource = Join-Path $PackageDir "Sts2Mcp.pck"
$manifestSource = Join-Path $PackageDir "PckRoot\\mod_manifest.json"
if (-not (Test-Path $dllSource -PathType Leaf)) {
    throw "Missing installer file: $dllSource"
}
if (-not (Test-Path $pckSource -PathType Leaf)) {
    throw "Missing installer file: $pckSource"
}
if (-not (Test-Path $manifestSource -PathType Leaf)) {
    throw "Missing installer file: $manifestSource"
}

$modsDir = Join-Path $resolvedGamePath "mods"
$targetDir = Join-Path $modsDir "Sts2Mcp"
$backupDir = Join-Path $modsDir "Sts2Mcp_backup"
$targetPckRootDir = Join-Path $targetDir "PckRoot"
New-Item -ItemType Directory -Path $modsDir -Force | Out-Null

if (Test-Path $targetDir -PathType Container) {
    if (Test-Path $backupDir -PathType Container) {
        Remove-Item $backupDir -Recurse -Force
    }

    Rename-Item $targetDir $backupDir
    Write-Host "[Sts2Mcp] Backed up existing installation to $backupDir"
}

try {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    New-Item -ItemType Directory -Path $targetPckRootDir -Force | Out-Null
    Copy-Item $dllSource (Join-Path $targetDir "Sts2Mcp.dll") -Force
    Copy-Item $pckSource (Join-Path $targetDir "Sts2Mcp.pck") -Force
    Copy-Item $manifestSource (Join-Path $targetPckRootDir "mod_manifest.json") -Force
}
catch {
    Write-Host "[Sts2Mcp] Install failed, restoring backup..."
    if (Test-Path $targetDir -PathType Container) {
        Remove-Item $targetDir -Recurse -Force
    }
    if (Test-Path $backupDir -PathType Container) {
        Rename-Item $backupDir $targetDir
    }
    throw
}

if (Test-Path $backupDir -PathType Container) {
    Remove-Item $backupDir -Recurse -Force
}

Write-Host "[Sts2Mcp] Install completed"
Write-Host ("  Game Path: {0}" -f $resolvedGamePath)
Write-Host ("  Mod Path : {0}" -f $targetDir)
Write-Host "[Sts2Mcp] Note: game save data is stored separately and is not modified by this installer."
