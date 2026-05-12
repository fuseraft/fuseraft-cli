#!/usr/bin/env bash
# Fuseraft CLI installer — Linux and macOS
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/install.sh | bash
#   bash install.sh [--system]   # install to /usr/local/bin instead of ~/.local/bin

set -euo pipefail

REPO="fuseraft/fuseraft-cli"
BINARY="fuseraft"

###############################################################################
# Flags
###############################################################################
SYSTEM_INSTALL=false
for arg in "$@"; do
  case "$arg" in
    --system) SYSTEM_INSTALL=true ;;
    *) echo "Unknown argument: $arg" >&2; exit 1 ;;
  esac
done

if $SYSTEM_INSTALL; then
  INSTALL_DIR="/usr/local/bin"
else
  INSTALL_DIR="${HOME}/.local/bin"
fi

###############################################################################
# Platform detection
###############################################################################
OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS" in
  Linux*)  OS_TAG="linux" ;;
  Darwin*) OS_TAG="osx"   ;;
  *)
    echo "ERROR: Unsupported OS: $OS" >&2
    echo "       Supported: Linux, macOS (Darwin)" >&2
    exit 1
    ;;
esac

case "$ARCH" in
  x86_64)          ARCH_TAG="x64"   ;;
  aarch64 | arm64) ARCH_TAG="arm64" ;;
  *)
    echo "ERROR: Unsupported architecture: $ARCH" >&2
    echo "       Supported: x86_64, aarch64/arm64" >&2
    exit 1
    ;;
esac

RID="${OS_TAG}-${ARCH_TAG}"

###############################################################################
# Resolve latest release version
###############################################################################
echo "Fetching latest release from github.com/${REPO}..."

if command -v curl &>/dev/null; then
  FETCH="curl -fsSL"
elif command -v wget &>/dev/null; then
  FETCH="wget -qO-"
else
  echo "ERROR: curl or wget is required." >&2
  exit 1
fi

API_URL="https://api.github.com/repos/${REPO}/releases/latest"
RELEASE_JSON=$($FETCH "$API_URL")

# Extract tag_name with basic sed/grep (no jq dependency)
TAG=$(echo "$RELEASE_JSON" | grep '"tag_name"' | sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')
if [[ -z "$TAG" ]]; then
  echo "ERROR: Could not determine the latest release tag." >&2
  echo "       Check https://github.com/${REPO}/releases" >&2
  exit 1
fi

VERSION="${TAG#v}"   # strip leading 'v'
ARCHIVE="fuseraft-${VERSION}-${RID}.tar.gz"
DOWNLOAD_URL="https://github.com/${REPO}/releases/download/${TAG}/${ARCHIVE}"

echo "Installing fuseraft ${VERSION} (${RID})..."

###############################################################################
# Download and extract
###############################################################################
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

echo "Downloading ${ARCHIVE}..."
$FETCH "$DOWNLOAD_URL" > "${TMP_DIR}/${ARCHIVE}"

echo "Extracting..."
tar -xzf "${TMP_DIR}/${ARCHIVE}" -C "$TMP_DIR"

###############################################################################
# Install
###############################################################################
if $SYSTEM_INSTALL; then
  SUDO=""
  if [[ "$(id -u)" -ne 0 ]]; then
    if command -v sudo &>/dev/null; then
      SUDO="sudo"
    else
      echo "ERROR: --system install requires root or sudo." >&2
      exit 1
    fi
  fi
  $SUDO mkdir -p "$INSTALL_DIR"
  $SUDO install -m 755 "${TMP_DIR}/${BINARY}" "${INSTALL_DIR}/${BINARY}"
else
  mkdir -p "$INSTALL_DIR"
  install -m 755 "${TMP_DIR}/${BINARY}" "${INSTALL_DIR}/${BINARY}"
fi

echo ""
echo "  fuseraft ${VERSION} installed to ${INSTALL_DIR}/${BINARY}"

###############################################################################
# PATH hint
###############################################################################
if ! echo ":${PATH}:" | grep -q ":${INSTALL_DIR}:"; then
  echo ""
  echo "  NOTE: ${INSTALL_DIR} is not in your PATH."
  echo "  Add the following line to your shell profile (~/.bashrc, ~/.zshrc, etc.):"
  echo ""
  echo "    export PATH=\"\$PATH:${INSTALL_DIR}\""
  echo ""
fi

echo "  Run 'fuseraft --version' to verify the installation."
echo ""
