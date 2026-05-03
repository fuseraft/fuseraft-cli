#!/usr/bin/env bash
# Fuseraft CLI build bootstrapper (Linux/macOS)
#
# Usage:
#   ./build.sh                           # Run Default target (Publish)
#   ./build.sh --target=Build
#   ./build.sh --target=Pack --runtime=linux-x64
#   ./build.sh --configuration=Debug --target=Test
#   ./build.sh --target=Lint

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

###############################################################################
# 1. Require dotnet SDK
###############################################################################
if ! command -v dotnet &>/dev/null; then
  echo "ERROR: 'dotnet' not found. Install the .NET SDK from https://dot.net" >&2
  exit 1
fi

DOTNET_VERSION=$(dotnet --version)
echo "Using .NET SDK $DOTNET_VERSION"

###############################################################################
# 2. Restore local tools (includes cake.tool)
###############################################################################
echo "Restoring local dotnet tools..."
dotnet tool restore --verbosity quiet

###############################################################################
# 3. Run Cake
###############################################################################
echo "Starting Cake build..."
dotnet cake build.cake "$@"
