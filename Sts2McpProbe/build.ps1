param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $projectDir "..\..")
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (!(Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

Write-Host "Building Sts2Mcp ($Configuration)..."
& $dotnet build (Join-Path $projectDir "Sts2Mcp.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Build failed. See compiler errors above."
}

$sourceDll = Join-Path $projectDir "bin\$Configuration\net9.0\Sts2Mcp.dll"
if (!(Test-Path $sourceDll)) {
    throw "Build succeeded but dll not found: $sourceDll"
}

$modDir = Join-Path $repoRoot "mods\Sts2Mcp"
$pckRootDir = Join-Path $modDir "PckRoot"
New-Item -ItemType Directory -Path $modDir -Force | Out-Null
New-Item -ItemType Directory -Path $pckRootDir -Force | Out-Null

Copy-Item $sourceDll (Join-Path $modDir "Sts2Mcp.dll") -Force
Copy-Item (Join-Path $projectDir "PckRoot\mod_manifest.json") (Join-Path $pckRootDir "mod_manifest.json") -Force

$gameExe = Join-Path $repoRoot "SlayTheSpire2.exe"
$packerScript = Join-Path $projectDir "tools\pack_pck.gd"
$outPck = Join-Path $modDir "Sts2Mcp.pck"

Write-Host "Packing PCK..."
& $gameExe --headless --script $packerScript -- $pckRootDir $outPck
if ($LASTEXITCODE -ne 0) {
    throw "PCK packing failed."
}
if (!(Test-Path $outPck)) {
    throw "PCK not found after packing: $outPck"
}

Write-Host ""
Write-Host "Done."
Write-Host "DLL: $modDir\Sts2Mcp.dll"
Write-Host "PCK: $modDir\Sts2Mcp.pck"
Write-Host "PCK root source: $pckRootDir\mod_manifest.json"
