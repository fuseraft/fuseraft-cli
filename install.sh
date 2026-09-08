#!/usr/bin/env bash
# fuseraft CLI installer — Linux and macOS
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
# Dependency checks
###############################################################################
if ! command -v tar >/dev/null 2>&1; then
  echo "ERROR: tar is required." >&2
  exit 1
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
# Downloader — array avoids command-string quoting pitfalls
###############################################################################
if command -v curl >/dev/null 2>&1; then
  FETCH_CMD=(curl -fsSL --retry 3 --retry-delay 1 --retry-connrefused)
elif command -v wget >/dev/null 2>&1; then
  FETCH_CMD=(wget -qO-)
else
  echo "ERROR: curl or wget is required." >&2
  exit 1
fi

###############################################################################
# Resolve latest release
###############################################################################
echo "Fetching latest release from github.com/${REPO}..."

# Resolve the tag from the redirect target of /releases/latest instead of the
# GitHub API: a HEAD-sized request instead of a multi-KB JSON body, and it
# isn't subject to the unauthenticated API's 60-requests/hour rate limit.
TAG=""
if [[ "${FETCH_CMD[0]}" == "curl" ]]; then
  FINAL_URL="$(curl -fsSIL -o /dev/null -w '%{url_effective}' "https://github.com/${REPO}/releases/latest" 2>/dev/null || true)"
  CANDIDATE_TAG="${FINAL_URL##*/}"
  [[ "$CANDIDATE_TAG" == v* ]] && TAG="$CANDIDATE_TAG"
fi

if [[ -z "$TAG" ]]; then
  RELEASE_JSON="$("${FETCH_CMD[@]}" "https://api.github.com/repos/${REPO}/releases/latest")"
  TAG="$(printf '%s\n' "$RELEASE_JSON" | grep '"tag_name"' | head -n1 | \
    sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')"
fi

if [[ -z "$TAG" ]]; then
  echo "ERROR: Could not determine the latest release tag." >&2
  echo "       Check https://github.com/${REPO}/releases" >&2
  exit 1
fi

VERSION="${TAG#v}"
ARCHIVE="fuseraft-${VERSION}-${RID}.tar.gz"
DOWNLOAD_URL="https://github.com/${REPO}/releases/download/${TAG}/${ARCHIVE}"

# UX: show current version when updating
if command -v "$BINARY" >/dev/null 2>&1; then
  CURRENT_VERSION="$("$BINARY" --version 2>/dev/null || true)"
  echo "Updating${CURRENT_VERSION:+ from ${CURRENT_VERSION}} to fuseraft ${VERSION} (${RID})..."
else
  echo "Installing fuseraft ${VERSION} (${RID})..."
fi

###############################################################################
# Download and extract
###############################################################################
TMP_DIR="$(mktemp -d 2>/dev/null || mktemp -d -t fuseraft)"
trap 'rm -rf "$TMP_DIR"' EXIT

echo "Downloading ${ARCHIVE}..."
if ! "${FETCH_CMD[@]}" "$DOWNLOAD_URL" > "${TMP_DIR}/${ARCHIVE}"; then
  rm -f "${TMP_DIR}/${ARCHIVE}"
  echo "ERROR: Failed to download '${ARCHIVE}'." >&2
  echo "       It may not exist for release ${TAG} — check https://github.com/${REPO}/releases/tag/${TAG}" >&2
  exit 1
fi

echo "Extracting..."
tar -xzf "${TMP_DIR}/${ARCHIVE}" -C "$TMP_DIR"

# Locate the binary regardless of archive folder layout
BIN_PATH="$(find "$TMP_DIR" -type f -name "$BINARY" | head -n 1)"
if [[ -z "$BIN_PATH" ]]; then
  echo "ERROR: ${BINARY} not found in archive. The release may be malformed." >&2
  exit 1
fi

###############################################################################
# Atomic install: write .new then move into place
###############################################################################
TMP_TARGET="${INSTALL_DIR}/${BINARY}.new"

if $SYSTEM_INSTALL; then
  SUDO=""
  if [[ "$(id -u)" -ne 0 ]]; then
    if command -v sudo >/dev/null 2>&1; then
      SUDO="sudo"
    else
      echo "ERROR: --system install requires root or sudo." >&2
      exit 1
    fi
  fi
  $SUDO mkdir -p "$INSTALL_DIR"
  $SUDO install -m 755 "$BIN_PATH" "$TMP_TARGET"
  $SUDO mv -f "$TMP_TARGET" "${INSTALL_DIR}/${BINARY}"
else
  mkdir -p "$INSTALL_DIR"
  install -m 755 "$BIN_PATH" "$TMP_TARGET"
  mv -f "$TMP_TARGET" "${INSTALL_DIR}/${BINARY}"
fi

echo ""
echo "  fuseraft ${VERSION} installed to ${INSTALL_DIR}/${BINARY}"

###############################################################################
# PATH hint — case avoids regex metacharacter false positives
###############################################################################
case ":${PATH}:" in
  *":${INSTALL_DIR}:"*)
    ;;
  *)
    SHELL_NAME="$(basename "${SHELL:-sh}")"
    echo ""
    echo "  NOTE: ${INSTALL_DIR} is not in your PATH."
    echo "  Add the following line to your ~/.${SHELL_NAME}rc (or equivalent):"
    echo ""
    echo "    export PATH=\"\$PATH:${INSTALL_DIR}\""
    echo ""
    ;;
esac

echo "  Run 'fuseraft --version' to verify the installation."
echo ""
