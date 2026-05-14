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

$Repo   = 'fuseraft/fuseraft-cli'
$RID    = 'win-x64'

###############################################################################
# Resolve latest release
###############################################################################
Write-Host "Fetching latest release from github.com/$Repo..."

$ApiUrl  = "https://api.github.com/repos/$Repo/releases/latest"
$Headers = @{ 'User-Agent' = 'fuseraft-installer' }

try {
    $Release = Invoke-RestMethod -Uri $ApiUrl -Headers $Headers
} catch {
    Write-Error "Could not reach GitHub API: $_"
    exit 1
}

$Tag     = $Release.tag_name
$Version = $Tag -replace '^v', ''
$Archive = "fuseraft-$Version-$RID.zip"
$DownloadUrl = "https://github.com/$Repo/releases/download/$Tag/$Archive"

Write-Host "Installing fuseraft $Version ($RID)..."

###############################################################################
# Download
###############################################################################
$TmpDir  = Join-Path $env:TEMP "fuseraft-install-$([System.IO.Path]::GetRandomFileName())"
New-Item -ItemType Directory -Path $TmpDir | Out-Null

$ZipPath = Join-Path $TmpDir $Archive
Write-Host "Downloading $Archive..."

try {
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipPath -Headers $Headers
} catch {
    Write-Error "Download failed: $_"
    exit 1
} finally {
    # cleanup on any exit
    Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action { Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue } | Out-Null
}

###############################################################################
# Extract and install
###############################################################################
Write-Host "Extracting..."
Expand-Archive -Path $ZipPath -DestinationPath $TmpDir -Force

$ExeSource = Join-Path $TmpDir 'fuseraft.exe'
if (-not (Test-Path $ExeSource)) {
    Write-Error "fuseraft.exe not found in archive. The release may be malformed."
    exit 1
}

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}

Copy-Item -Path $ExeSource -Destination (Join-Path $InstallDir 'fuseraft.exe') -Force
Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "  fuseraft $Version installed to $InstallDir\fuseraft.exe"

###############################################################################
# Add to user PATH if not already present
###############################################################################
$UserPath = [System.Environment]::GetEnvironmentVariable('PATH', 'User') ?? ''

if ($UserPath -split ';' | Where-Object { $_ -eq $InstallDir }) {
    # already on PATH — nothing to do
} else {
    $NewPath = ($UserPath.TrimEnd(';') + ";$InstallDir").TrimStart(';')
    [System.Environment]::SetEnvironmentVariable('PATH', $NewPath, 'User')
    Write-Host "  Added $InstallDir to your user PATH."
    Write-Host "  Restart your terminal (or open a new one) to use fuseraft."
}

Write-Host ""
Write-Host "  Run 'fuseraft --version' to verify the installation."
Write-Host ""
