#!/bin/bash

set -e

export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH"

if [ $# -ne 4 ]; then
    echo "Usage: build-macos-desktop.sh <source-archive> <work-directory> <artifacts-directory> <version>"
    exit 1
fi

SOURCE_ARCHIVE="$1"
WORK_DIRECTORY="$2"
ARTIFACTS_DIRECTORY="$3"
VERSION="$4"

if [ "$(uname -s)" != "Darwin" ] || [ "$(uname -m)" != "arm64" ]; then
    echo "The macOS desktop worker must be an Apple Silicon Mac."
    exit 1
fi

rm -rf "$WORK_DIRECTORY" "$ARTIFACTS_DIRECTORY"
mkdir -p "$WORK_DIRECTORY" "$ARTIFACTS_DIRECTORY"
ditto -x -k "$SOURCE_ARCHIVE" "$WORK_DIRECTORY"

DESKTOP_DIRECTORY="$WORK_DIRECTORY/RemuxForge.Desktop"
pushd "$DESKTOP_DIRECTORY" > /dev/null
npm ci
npm run prepare-sidecar -- osx-arm64 "$VERSION"
CI=true npx tauri build --target aarch64-apple-darwin --bundles dmg --config "{\"version\":\"$VERSION\"}"

DMG_DIRECTORY="src-tauri/target/aarch64-apple-darwin/release/bundle/dmg"
DMG_PATH="$(find "$DMG_DIRECTORY" -maxdepth 1 -type f -name '*.dmg' -print -quit)"
if [ -z "$DMG_PATH" ]; then
    echo "macOS DMG not found in: $DMG_DIRECTORY"
    exit 1
fi

cp "$DMG_PATH" "$ARTIFACTS_DIRECTORY/RemuxForge-Desktop-osx-arm64.dmg"
popd > /dev/null
