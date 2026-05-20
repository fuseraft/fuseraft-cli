#Requires -Version 5.1
<#
.SYNOPSIS
    fuseraft CLI installer for Windows.

.DESCRIPTION
    Downloads the latest fuseraft release from GitHub and installs it to
    %LOCALAPPDATA%\fuseraft\bin, then adds that directory to the user PATH.

.EXAMPLE
    irm https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/install.ps1 | iex

.EXAMPLE
    # Or run locally:
    .\install.ps1

.EXAMPLE
    # Install to a custom directory:
    .\install.ps1 -InstallDir "C:\Tools\fuseraft"
#>
param(
    [string] $InstallDir = "$env:LOCALAPPDATA\fuseraft\bin"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure TLS 1.2 for older Windows / PS 5.1 environments
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'fuseraft/fuseraft-cli'

# Detect architecture via environment variable (works on all PS 5.1 hosts)
switch ($env:PROCESSOR_ARCHITECTURE) {
    'AMD64' { $RID = 'win-x64' }
    'ARM64' { $RID = 'win-arm64' }
    default {
        Write-Error "Unsupported architecture: $($env:PROCESSOR_ARCHITECTURE)"
        exit 1
    }
}

# Validate and normalize InstallDir
try {
    $InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
} catch {
    Write-Error ("Invalid install directory: " + $_.Exception.Message)
    exit 1
}

###############################################################################
# Resolve latest release
###############################################################################
Write-Host "Fetching latest release from github.com/$Repo..."

$ApiUrl  = "https://api.github.com/repos/$Repo/releases/latest"
$Headers = @{ 'User-Agent' = 'fuseraft-installer' }

try {
    $Release = Invoke-RestMethod -Uri $ApiUrl -Headers $Headers
} catch {
    Write-Error ("Could not reach GitHub API: " + $_.Exception.Message)
    exit 1
}

$Tag     = $Release.tag_name
$Version = $Tag -replace '^v', ''
$Archive = "fuseraft-$Version-$RID.zip"

# Validate the expected asset exists in the release
$Asset = $Release.assets | Where-Object { $_.name -eq $Archive }
if (-not $Asset) {
    Write-Error "Release asset '$Archive' not found in release $Tag."
    exit 1
}

$DownloadUrl = $Asset.browser_download_url
$TargetExe   = Join-Path $InstallDir 'fuseraft.exe'

if (Test-Path $TargetExe) {
    Write-Host "Updating existing installation to fuseraft $Version ($RID)..."
} else {
    Write-Host "Installing fuseraft $Version ($RID)..."
}

###############################################################################
# Download, extract, install
###############################################################################
$TmpDir = Join-Path $env:TEMP "fuseraft-install-$([System.IO.Path]::GetRandomFileName())"
New-Item -ItemType Directory -Path $TmpDir | Out-Null

try {
    $ZipPath = Join-Path $TmpDir $Archive
    Write-Host "Downloading $Archive..."

    try {
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipPath -Headers $Headers
    } catch {
        Write-Error ("Download failed: " + $_.Exception.Message)
        exit 1
    }

    Write-Host "Extracting..."

    # Retry extraction — AV/file-lock pressure can cause transient failures
    for ($i = 0; $i -lt 3; $i++) {
        try {
            Expand-Archive -Path $ZipPath -DestinationPath $TmpDir -Force
            break
        } catch {
            if ($i -eq 2) { throw }
            Start-Sleep -Milliseconds 500
        }
    }

    # Locate the executable regardless of zip folder structure
    $ExeSource = Get-ChildItem -Path $TmpDir -Recurse -Filter 'fuseraft.exe' |
        Select-Object -First 1 -ExpandProperty FullName

    if (-not $ExeSource) {
        Write-Error "fuseraft.exe not found in archive. The release may be malformed."
        exit 1
    }

    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir | Out-Null
    }

    # Atomic replacement: write to .new then move over the target
    $TempExe = "$TargetExe.new"
    Copy-Item $ExeSource $TempExe -Force
    Move-Item $TempExe $TargetExe -Force

    Write-Host ""
    Write-Host "  fuseraft $Version installed to $TargetExe"
} finally {
    Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue
}

###############################################################################
# Add to user PATH if not already present
###############################################################################
$UserPath = [System.Environment]::GetEnvironmentVariable('PATH', 'User')
if ($null -eq $UserPath) {
    $UserPath = ''
}

$NormalizedInstallDir = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$PathEntries = $UserPath -split ';' |
    Where-Object { $_ } |
    ForEach-Object {
        try { [System.IO.Path]::GetFullPath($_).TrimEnd('\') } catch { $_ }
    }

if ($PathEntries -contains $NormalizedInstallDir) {
    # already on PATH — nothing to do
} else {
    $NewPath = ($UserPath.TrimEnd(';') + ";$InstallDir").TrimStart(';')
    if ($NewPath.Length -gt 2048) {
        Write-Warning "User PATH is very large and may exceed Windows environment limits."
    }
    [System.Environment]::SetEnvironmentVariable('PATH', $NewPath, 'User')
    Write-Host "  Added $InstallDir to your user PATH."
    Write-Host "  Restart your terminal (or open a new one) to use fuseraft."
}

Write-Host ""
Write-Host "  Run 'fuseraft --version' to verify the installation."
Write-Host ""
