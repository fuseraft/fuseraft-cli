#Requires -Version 5.1
<#
.SYNOPSIS
    Fuseraft CLI build bootstrapper (Windows PowerShell / pwsh)

.DESCRIPTION
    Restores the cake.tool local tool and runs the Cake build script.

.PARAMETER Target
    The Cake target to run. Defaults to "Default" (full pipeline).

.PARAMETER Configuration
    Build configuration: Release (default) or Debug.

.PARAMETER Runtime
    .NET runtime identifier for a self-contained publish, e.g. win-x64.

.PARAMETER SkipTests
    When set, skip the Test task.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Target Build
    .\build.ps1 -Target Pack -Runtime win-x64
    .\build.ps1 -Configuration Debug -Target Test
#>
[CmdletBinding()]
param(
    [string] $Target        = "Default",
    [string] $Configuration = "Release",
    [string] $Runtime       = "",
    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot

try {
    ###########################################################################
    # 1. Require dotnet SDK
    ###########################################################################
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error "ERROR: 'dotnet' not found. Install the .NET SDK from https://dot.net"
        exit 1
    }

    $dotnetVersion = dotnet --version
    Write-Host "Using .NET SDK $dotnetVersion"

    ###########################################################################
    # 2. Restore local tools (includes cake.tool)
    ###########################################################################
    Write-Host "Restoring local dotnet tools..."
    dotnet tool restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    ###########################################################################
    # 3. Build argument list for Cake
    ###########################################################################
    $cakeArgs = @(
        "build.cake"
        "--target=$Target"
        "--configuration=$Configuration"
    )

    if ($Runtime) {
        $cakeArgs += "--runtime=$Runtime"
    }

    if ($SkipTests) {
        $cakeArgs += "--skipTests=true"
    }

    ###########################################################################
    # 4. Run Cake
    ###########################################################################
    Write-Host "Starting Cake build (target: $Target)..."
    dotnet cake @cakeArgs
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
