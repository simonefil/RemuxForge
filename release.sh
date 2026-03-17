#!/bin/bash
# MergeLanguageTracks release script
# Usage: ./release.sh <tag> <notes>

set -e

if [ $# -lt 2 ]; then
    echo "Usage: ./release.sh <tag> <notes>"
    exit 1
fi

TAG="$1"
NOTES="$2"

PROJECT="MergeLanguageTracks.csproj"
ARTIFACTS_DIR="release-artifacts"
PUBLISH_DIR="publish"
VERSION="${TAG#v}"

RIDS=("win-x64" "linux-x64" "linux-arm64" "osx-x64" "osx-arm64")

confirm_step() {
    read -r -p "$1 [Y/n] " choice
    if [ "$choice" = "n" ] || [ "$choice" = "N" ]; then
        echo "Aborted."
        exit 0
    fi
}

cleanup() {
    rm -rf "$PUBLISH_DIR" "$ARTIFACTS_DIR"
}

trap cleanup EXIT

# Clean previous builds
cleanup
mkdir -p "$ARTIFACTS_DIR"

# Build all targets
confirm_step "Build binaries for ${#RIDS[@]} platforms?"
for rid in "${RIDS[@]}"; do
    echo "Building $rid..."

    dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=false \
        -p:EnableCompressionInSingleFile=true \
        -p:Version="$VERSION" \
        -o "$PUBLISH_DIR/$rid"

    cd "$PUBLISH_DIR/$rid"
    zip -r "../../$ARTIFACTS_DIR/MergeLanguageTracks-$rid.zip" .
    cd ../..

    echo "$rid done."
done

# Create and push tag
confirm_step "Create git tag $TAG and push?"
echo "Creating tag $TAG..."
if ! git tag "$TAG"; then
    echo "Failed to create tag (already exists?)"
    exit 1
fi
if ! git push origin "$TAG"; then
    echo "Failed to push tag, removing local tag..."
    git tag -d "$TAG"
    exit 1
fi

# Create GitHub release
confirm_step "Create GitHub release with artifacts?"
echo "Creating GitHub release..."
if ! gh release create "$TAG" "$ARTIFACTS_DIR"/*.zip --title "$TAG" --notes "$NOTES"; then
    echo "Failed to create release, removing tag..."
    git push origin --delete "$TAG"
    git tag -d "$TAG"
    exit 1
fi

echo "Release $TAG published successfully."
