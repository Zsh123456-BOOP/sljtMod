param(
    [string]$GamePath,
    [switch]$AutoCloseGame
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "InstallerCommon.ps1")

Assert-GameNotRunning -AutoClose:$AutoCloseGame

$resolvedGamePath = Resolve-Sts2GamePath -PreferredPath $GamePath
if (-not $resolvedGamePath) {
    throw "Could not auto-detect game path. Please pass it manually: Uninstall-Sts2Mcp.bat ""D:\SteamLibrary\steamapps\common\Slay the Spire 2"""
}

$modsDir = Join-Path $resolvedGamePath "mods"
$targetDir = Join-Path $modsDir "Sts2Mcp"
if (Test-Path $targetDir -PathType Container) {
    Remove-Item $targetDir -Recurse -Force
    Write-Host ("[Sts2Mcp] Uninstalled: {0}" -f $targetDir)
} else {
    Write-Host "[Sts2Mcp] Not installed."
}
