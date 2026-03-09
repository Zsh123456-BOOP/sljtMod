Set-StrictMode -Version Latest

function Test-Sts2GamePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    $exePath = Join-Path $Path "SlayTheSpire2.exe"
    $corePath = Join-Path $Path "data_sts2_windows_x86_64\sts2.dll"
    return (Test-Path $exePath -PathType Leaf) -and (Test-Path $corePath -PathType Leaf)
}

function Normalize-PathCandidate {
    param(
        [AllowNull()]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $clean = $Path.Trim().Trim('"')
    $clean = $clean -replace '/', '\'
    $clean = $clean -replace '\\\\', '\'
    return $clean
}

function Get-SteamRootCandidates {
    $candidates = New-Object System.Collections.Generic.List[string]

    $defaultCandidates = @(
        $env:STEAM_PATH,
        "C:\Program Files (x86)\Steam",
        "C:\Program Files\Steam"
    )
    foreach ($candidate in $defaultCandidates) {
        $normalized = Normalize-PathCandidate $candidate
        if ($normalized) {
            $candidates.Add($normalized)
        }
    }

    $registryPaths = @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )

    foreach ($regPath in $registryPaths) {
        $item = Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue
        if (-not $item) {
            continue
        }

        foreach ($propName in @("SteamPath", "InstallPath")) {
            $value = $item.$propName
            $normalized = Normalize-PathCandidate $value
            if ($normalized) {
                $candidates.Add($normalized)
            }
        }
    }

    $unique = New-Object System.Collections.Generic.List[string]
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        if ($seen.Add($candidate) -and (Test-Path $candidate -PathType Container)) {
            $unique.Add($candidate)
        }
    }

    return $unique
}

function Get-SteamLibraryPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SteamRoot
    )

    $libraries = New-Object System.Collections.Generic.List[string]
    $libraries.Add($SteamRoot)

    $libraryVdfPath = Join-Path $SteamRoot "steamapps\libraryfolders.vdf"
    if (Test-Path $libraryVdfPath -PathType Leaf) {
        try {
            $content = Get-Content $libraryVdfPath -Raw -Encoding UTF8
            $matches = [regex]::Matches($content, '"path"\s*"([^"]+)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            foreach ($match in $matches) {
                $libraryPath = Normalize-PathCandidate $match.Groups[1].Value
                if ($libraryPath) {
                    $libraries.Add($libraryPath)
                }
            }
        } catch {
            # Ignore parse failures.
        }
    }

    $unique = New-Object System.Collections.Generic.List[string]
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $libraries) {
        if ($seen.Add($library) -and (Test-Path $library -PathType Container)) {
            $unique.Add($library)
        }
    }

    return $unique
}

function Get-AppInstallDirFromManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    if (-not (Test-Path $ManifestPath -PathType Leaf)) {
        return $null
    }

    try {
        $content = Get-Content $ManifestPath -Raw -Encoding UTF8
        $match = [regex]::Match($content, '"installdir"\s*"([^"]+)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($match.Success) {
            return Normalize-PathCandidate $match.Groups[1].Value
        }
    } catch {
        # Ignore parse failures.
    }

    return $null
}

function Resolve-Sts2GamePath {
    param(
        [string]$PreferredPath
    )

    $preferred = Normalize-PathCandidate $PreferredPath
    if ($preferred -and (Test-Sts2GamePath -Path $preferred)) {
        return $preferred
    }

    $envPreferred = Normalize-PathCandidate $env:STS2_GAME_PATH
    if ($envPreferred -and (Test-Sts2GamePath -Path $envPreferred)) {
        return $envPreferred
    }

    $appId = 2868840
    $gameFolderName = "Slay the Spire 2"
    foreach ($steamRoot in Get-SteamRootCandidates) {
        foreach ($library in Get-SteamLibraryPaths -SteamRoot $steamRoot) {
            $steamApps = Join-Path $library "steamapps"
            $manifest = Join-Path $steamApps ("appmanifest_{0}.acf" -f $appId)
            $installDirName = Get-AppInstallDirFromManifest -ManifestPath $manifest

            if ($installDirName) {
                $candidate = Join-Path $steamApps ("common\{0}" -f $installDirName)
                if (Test-Sts2GamePath -Path $candidate) {
                    return $candidate
                }
            }

            $fallback = Join-Path $steamApps ("common\{0}" -f $gameFolderName)
            if (Test-Sts2GamePath -Path $fallback) {
                return $fallback
            }
        }
    }

    return $null
}

function Assert-GameNotRunning {
    $running = Get-Process -Name "SlayTheSpire2" -ErrorAction SilentlyContinue
    if ($running) {
        throw "SlayTheSpire2 is running. Please close the game before install/uninstall."
    }
}
