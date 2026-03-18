param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $projectDir "..\..")

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $projectDir "dist"
}

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (!(Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

Write-Host "[Sts2Mcp] Building project..."
& $dotnet build (Join-Path $projectDir "Sts2Mcp.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Dotnet build failed."
}

$dllSource = Join-Path $projectDir "bin\$Configuration\net9.0\Sts2Mcp.dll"
if (!(Test-Path $dllSource -PathType Leaf)) {
    throw "Built dll not found: $dllSource"
}

$pckSource = Join-Path $repoRoot "mods\Sts2Mcp\Sts2Mcp.pck"
if (!(Test-Path $pckSource -PathType Leaf)) {
    Write-Host "[Sts2Mcp] Existing pck not found, trying build.ps1 to generate..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectDir "build.ps1") -Configuration $Configuration
}
if (!(Test-Path $pckSource -PathType Leaf)) {
    throw "PCK not found: $pckSource. Please build once in game environment first."
}

$stageDir = Join-Path $OutputDir "Sts2McpInstaller"
if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stageDir "PckRoot") -Force | Out-Null

Copy-Item $dllSource (Join-Path $stageDir "Sts2Mcp.dll") -Force
Copy-Item $pckSource (Join-Path $stageDir "Sts2Mcp.pck") -Force
Copy-Item (Join-Path $projectDir "PckRoot\\mod_manifest.json") (Join-Path $stageDir "PckRoot\\mod_manifest.json") -Force
Copy-Item (Join-Path $projectDir "tools\InstallerCommon.ps1") (Join-Path $stageDir "InstallerCommon.ps1") -Force
Copy-Item (Join-Path $projectDir "tools\Install-Sts2Mcp.ps1") (Join-Path $stageDir "Install-Sts2Mcp.ps1") -Force
Copy-Item (Join-Path $projectDir "tools\Uninstall-Sts2Mcp.ps1") (Join-Path $stageDir "Uninstall-Sts2Mcp.ps1") -Force
Copy-Item (Join-Path $projectDir "installer\Install-Sts2Mcp.bat") (Join-Path $stageDir "Install-Sts2Mcp.bat") -Force
Copy-Item (Join-Path $projectDir "installer\Uninstall-Sts2Mcp.bat") (Join-Path $stageDir "Uninstall-Sts2Mcp.bat") -Force
Copy-Item (Join-Path $projectDir "installer\README-install.txt") (Join-Path $stageDir "README-install.txt") -Force

$zipName = "Sts2McpInstaller_{0}.zip" -f (Get-Date -Format "yyyyMMdd_HHmmss")
$zipPath = Join-Path $OutputDir $zipName
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "[Sts2Mcp] Package ready:"
Write-Host "  Stage: $stageDir"
Write-Host "  Zip  : $zipPath"
