param(
    [string]$GamePath,
    [string]$PackageDir = $PSScriptRoot,
    [switch]$NoBackup
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "InstallerCommon.ps1")

Assert-GameNotRunning

$resolvedGamePath = Resolve-Sts2GamePath -PreferredPath $GamePath
if (-not $resolvedGamePath) {
    throw "Could not auto-detect game path. Please pass it manually: Install-Sts2Mcp.bat ""D:\SteamLibrary\steamapps\common\Slay the Spire 2"""
}

$dllSource = Join-Path $PackageDir "Sts2Mcp.dll"
$pckSource = Join-Path $PackageDir "Sts2Mcp.pck"
if (-not (Test-Path $dllSource -PathType Leaf)) {
    throw "Missing installer file: $dllSource"
}
if (-not (Test-Path $pckSource -PathType Leaf)) {
    throw "Missing installer file: $pckSource"
}

$modsDir = Join-Path $resolvedGamePath "mods"
$targetDir = Join-Path $modsDir "Sts2Mcp"
New-Item -ItemType Directory -Path $modsDir -Force | Out-Null

$backupDir = $null
if (Test-Path $targetDir -PathType Container) {
    if ($NoBackup) {
        Remove-Item $targetDir -Recurse -Force
    } else {
        $backupDir = "{0}.backup.{1}" -f $targetDir, (Get-Date -Format "yyyyMMdd_HHmmss")
        Move-Item -Path $targetDir -Destination $backupDir
    }
}

New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Copy-Item $dllSource (Join-Path $targetDir "Sts2Mcp.dll") -Force
Copy-Item $pckSource (Join-Path $targetDir "Sts2Mcp.pck") -Force

Write-Host "[Sts2Mcp] Install completed"
Write-Host ("  Game Path: {0}" -f $resolvedGamePath)
Write-Host ("  Mod Path : {0}" -f $targetDir)
if ($backupDir) {
    Write-Host ("  Backup   : {0}" -f $backupDir)
}
