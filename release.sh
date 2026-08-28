#!/bin/bash
# RemuxForge release script
# Usage: ./release.sh <tag> <notes> [--build-only] [--target <target>]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

if [ -f "$SCRIPT_DIR/.env" ]; then
    set -a
    . "$SCRIPT_DIR/.env"
    set +a
fi

if [ $# -lt 2 ]; then
    echo "Usage: ./release.sh <tag> <notes> [--build-only] [--target <target>]"
    exit 1
fi

TAG="$1"
NOTES="$2"
shift 2

BUILD_ONLY=false
BUILD_TARGET="all"
while [ $# -gt 0 ]; do
    case "$1" in
        --build-only)
            BUILD_ONLY=true
            ;;
        --target)
            if [ $# -lt 2 ]; then
                echo "Missing value for --target."
                exit 1
            fi
            BUILD_TARGET="$2"
            shift
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: ./release.sh <tag> <notes> [--build-only] [--target <target>]"
            exit 1
            ;;
    esac
    shift
done

case "$BUILD_TARGET" in
    all|desktop-osx-arm64|desktop-win-x64)
        ;;
    *)
        echo "Unsupported target: $BUILD_TARGET"
        echo "Supported targets: all, desktop-osx-arm64, desktop-win-x64"
        exit 1
        ;;
esac

if [ "$BUILD_TARGET" != "all" ] && [ "$BUILD_ONLY" != true ]; then
    echo "--target can only be used together with --build-only."
    exit 1
fi

CLI_PROJECT="RemuxForge.Cli/RemuxForge.Cli.csproj"
WEB_PROJECT="RemuxForge.Web/RemuxForge.Web.csproj"
DESKTOP_DIR="RemuxForge.Desktop"
ARTIFACTS_DIR="release-artifacts"
PUBLISH_DIR="publish"
DOCKER_IMAGE="draknodd/remuxforge"
VERSION="${TAG#v}"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"
MACOS_SSH_PORT="${MACOS_SSH_PORT:-22}"
WINDOWS_SSH_PORT="${WINDOWS_SSH_PORT:-22}"
SOURCE_ARCHIVE="$SCRIPT_DIR/$PUBLISH_DIR/remuxforge-source-$VERSION.zip"

RIDS=("win-x64" "linux-x64" "linux-arm64" "osx-x64" "osx-arm64")

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]]; then
    echo "Version must be a valid release version, for example v1.2.3."
    exit 1
fi

if [ "$(uname -s)" != "Linux" ]; then
    echo "release.sh must be executed on the Linux release VM."
    exit 1
fi

require_command() {
    if ! command -v "$1" > /dev/null 2>&1; then
        echo "Missing required command: $1"
        exit 1
    fi
}

if [ "$BUILD_TARGET" = "all" ]; then
    require_command glslc
    require_command spirv-val
fi

build_macos_desktop() {
    local identity_file
    local password
    local remote_directory
    local remote_target
    local scp_command
    local ssh_options
    local ssh_command
    local scp_options

    if [ -z "${MACOS_SSH_HOST:-}" ] || [ -z "${MACOS_SSH_USER:-}" ]; then
        echo "Set MACOS_SSH_HOST and MACOS_SSH_USER in .env."
        exit 1
    fi

    if ! [[ "$MACOS_SSH_PORT" =~ ^[0-9]+$ ]]; then
        echo "MACOS_SSH_PORT must be numeric."
        exit 1
    fi

    require_command ssh
    require_command scp

    remote_target="${MACOS_SSH_USER}@${MACOS_SSH_HOST}"
    ssh_command=(ssh)
    scp_command=(scp)
    ssh_options=(-p "$MACOS_SSH_PORT" -o BatchMode=yes -o StrictHostKeyChecking=accept-new)
    scp_options=(-P "$MACOS_SSH_PORT" -o BatchMode=yes -o StrictHostKeyChecking=accept-new)

    password="${MACOS_SSH_PASSWORD:-}"
    if [ -n "$password" ]; then
        require_command sshpass
        export SSHPASS="$password"
        ssh_command=(sshpass -e ssh)
        scp_command=(sshpass -e scp)
        ssh_options=(-p "$MACOS_SSH_PORT" -o StrictHostKeyChecking=accept-new)
        scp_options=(-P "$MACOS_SSH_PORT" -o StrictHostKeyChecking=accept-new)
    fi

    identity_file="${MACOS_SSH_IDENTITY_FILE:-}"
    if [ -n "$identity_file" ]; then
        if [ ! -f "$identity_file" ]; then
            echo "MACOS_SSH_IDENTITY_FILE not found: $identity_file"
            exit 1
        fi
        ssh_options+=(-i "$identity_file")
        scp_options+=(-i "$identity_file")
    fi

    remote_directory="$("${ssh_command[@]}" "${ssh_options[@]}" "$remote_target" "/usr/bin/mktemp -d '/tmp/remuxforge-release.XXXXXX'")"
    if [ -z "$remote_directory" ]; then
        echo "Unable to create the macOS temporary build directory."
        exit 1
    fi

    "${scp_command[@]}" "${scp_options[@]}" "$SOURCE_ARCHIVE" "$remote_target:$remote_directory/source.zip"
    "${scp_command[@]}" "${scp_options[@]}" "$DESKTOP_DIR/scripts/build-macos-desktop.sh" "$remote_target:$remote_directory/build-macos-desktop.sh"

    if ! "${ssh_command[@]}" "${ssh_options[@]}" "$remote_target" "/bin/bash '$remote_directory/build-macos-desktop.sh' '$remote_directory/source.zip' '$remote_directory/work' '$remote_directory/artifacts' '$VERSION'"; then
        echo "macOS build failed. Remote diagnostics remain in $remote_directory."
        exit 1
    fi

    "${scp_command[@]}" "${scp_options[@]}" "$remote_target:$remote_directory/artifacts/RemuxForge-Desktop-osx-arm64.dmg" "$ARTIFACTS_DIR/"
    "${ssh_command[@]}" "${ssh_options[@]}" "$remote_target" "rm -rf '$remote_directory'"
    unset SSHPASS
}

build_windows_desktop() {
    local identity_file
    local password
    local remote_directory
    local remote_target
    local scp_command
    local ssh_options
    local ssh_command
    local scp_options

    if [ -z "${WINDOWS_SSH_HOST:-}" ] || [ -z "${WINDOWS_SSH_USER:-}" ]; then
        echo "Set WINDOWS_SSH_HOST and WINDOWS_SSH_USER in .env."
        exit 1
    fi

    if ! [[ "$WINDOWS_SSH_PORT" =~ ^[0-9]+$ ]]; then
        echo "WINDOWS_SSH_PORT must be numeric."
        exit 1
    fi

    require_command ssh
    require_command scp

    remote_target="${WINDOWS_SSH_USER}@${WINDOWS_SSH_HOST}"
    ssh_command=(ssh)
    scp_command=(scp)
    ssh_options=(-p "$WINDOWS_SSH_PORT" -o BatchMode=yes -o StrictHostKeyChecking=accept-new)
    scp_options=(-P "$WINDOWS_SSH_PORT" -o BatchMode=yes -o StrictHostKeyChecking=accept-new)

    password="${WINDOWS_SSH_PASSWORD:-}"
    if [ -n "$password" ]; then
        require_command sshpass
        export SSHPASS="$password"
        ssh_command=(sshpass -e ssh)
        scp_command=(sshpass -e scp)
        ssh_options=(-p "$WINDOWS_SSH_PORT" -o StrictHostKeyChecking=accept-new)
        scp_options=(-P "$WINDOWS_SSH_PORT" -o StrictHostKeyChecking=accept-new)
    fi

    identity_file="${WINDOWS_SSH_IDENTITY_FILE:-}"
    if [ -n "$identity_file" ]; then
        if [ ! -f "$identity_file" ]; then
            echo "WINDOWS_SSH_IDENTITY_FILE not found: $identity_file"
            exit 1
        fi
        ssh_options+=(-i "$identity_file")
        scp_options+=(-i "$identity_file")
    fi

    remote_directory="$("${ssh_command[@]}" "${ssh_options[@]}" "$remote_target" "powershell.exe -NoProfile -NonInteractive -Command \"[Console]::Write((New-Item -ItemType Directory -Path (Join-Path ([System.IO.Path]::GetTempPath()) ('RemuxForgeRelease-' + [guid]::NewGuid().ToString('N')))).FullName.Replace('\\','/'))\"")"
    if [ -z "$remote_directory" ]; then
        echo "Unable to create the Windows temporary build directory."
        exit 1
    fi

    "${scp_command[@]}" "${scp_options[@]}" "$SOURCE_ARCHIVE" "$remote_target:$remote_directory/source.zip"
    "${scp_command[@]}" "${scp_options[@]}" "$DESKTOP_DIR/scripts/build-windows-desktop.ps1" "$remote_target:$remote_directory/build-windows-desktop.ps1"

    if ! "${ssh_command[@]}" "${ssh_options[@]}" "$remote_target" "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $remote_directory/build-windows-desktop.ps1 -SourceArchive $remote_directory/source.zip -WorkDirectory $remote_directory/work -ArtifactsDirectory $remote_directory/artifacts -Version $VERSION"; then
        echo "Windows build failed. Remote diagnostics remain in $remote_directory."
        exit 1
    fi

    "${scp_command[@]}" "${scp_options[@]}" "$remote_target:$remote_directory/artifacts/RemuxForge-Desktop-win-x64.msi" "$ARTIFACTS_DIR/"
    "${ssh_command[@]}" "${ssh_options[@]}" "$remote_target" "powershell.exe -NoProfile -NonInteractive -Command \"Remove-Item -LiteralPath '$remote_directory' -Recurse -Force\""
    unset SSHPASS
}

confirm_step() {
    read -r -p "$1 [Y/n] " choice
    if [ "$choice" = "n" ] || [ "$choice" = "N" ]; then
        echo "Aborted."
        exit 0
    fi
}

cleanup() {
    rm -rf "$PUBLISH_DIR"
    if [ "$BUILD_ONLY" != true ]; then
        rm -rf "$ARTIFACTS_DIR"
    fi
}

trap cleanup EXIT

# Clean previous builds
rm -rf "$PUBLISH_DIR" "$ARTIFACTS_DIR"
mkdir -p "$PUBLISH_DIR" "$ARTIFACTS_DIR"

if [ "$BUILD_TARGET" = "all" ]; then
    # Build CLI for all targets
    confirm_step "Build CLI binaries for ${#RIDS[@]} platforms?"
    for rid in "${RIDS[@]}"; do
        echo "Building CLI $rid..."

        dotnet publish "$CLI_PROJECT" -c Release -r "$rid" --self-contained true \
            -p:PublishSingleFile=true \
            -p:PublishTrimmed=false \
            -p:EnableCompressionInSingleFile=true \
            -p:Version="$VERSION" \
            -o "$PUBLISH_DIR/cli/$rid"

        cd "$PUBLISH_DIR/cli/$rid"
        zip -r "../../../$ARTIFACTS_DIR/RemuxForge-Cli-$rid.zip" .
        cd ../../..

        echo "CLI $rid done."
    done
fi

if [ "$BUILD_TARGET" = "all" ] || [ "$BUILD_TARGET" = "desktop-osx-arm64" ] || [ "$BUILD_TARGET" = "desktop-win-x64" ]; then
    # Build the current local source tree on the selected native desktop workers.
    require_command ssh
    require_command scp
    require_command zip
    zip -q -r "$SOURCE_ARCHIVE" . \
        -x '.git/*' \
        -x '.env' \
        -x 'publish/*' \
        -x 'release-artifacts/*' \
        -x '*/bin/*' \
        -x '*/obj/*' \
        -x '*/node_modules/*' \
        -x '*/target/*'
fi

if [ "$BUILD_TARGET" = "all" ] || [ "$BUILD_TARGET" = "desktop-osx-arm64" ]; then
    confirm_step "Build Tauri Desktop bundle for osx-arm64 through SSH?"
    build_macos_desktop
fi

if [ "$BUILD_TARGET" = "all" ] || [ "$BUILD_TARGET" = "desktop-win-x64" ]; then
    confirm_step "Build Tauri Desktop MSI for win-x64 through SSH?"
    build_windows_desktop
fi

if [ "$BUILD_TARGET" = "all" ]; then
    # Build WebUI for all targets
    confirm_step "Build WebUI binaries for ${#RIDS[@]} platforms?"
    for rid in "${RIDS[@]}"; do
        echo "Building WebUI $rid..."

        dotnet publish "$WEB_PROJECT" -c Release -r "$rid" --self-contained true \
            -p:PublishSingleFile=true \
            -p:PublishTrimmed=false \
            -p:EnableCompressionInSingleFile=true \
            -p:Version="$VERSION" \
            -o "$PUBLISH_DIR/web/$rid"

        cd "$PUBLISH_DIR/web/$rid"
        zip -r "../../../$ARTIFACTS_DIR/RemuxForge-Web-$rid.zip" .
        cd ../../..

        echo "WebUI $rid done."
    done
fi

if [ "$BUILD_ONLY" != true ]; then
    # Build Docker image
    confirm_step "Build Docker image ${DOCKER_IMAGE}:${TAG}?"
    echo "Building Docker image..."
    docker build --platform "$DOCKER_PLATFORM" --build-arg VERSION="$VERSION" -t "${DOCKER_IMAGE}:${TAG}" -t "${DOCKER_IMAGE}:latest" .

    # Push Docker image
    confirm_step "Push Docker image to Docker Hub?"
    echo "Pushing Docker image..."
    docker push "${DOCKER_IMAGE}:${TAG}"
    docker push "${DOCKER_IMAGE}:latest"
    echo "Docker image pushed."
fi

# Generate checksums for every release artifact.
(
    cd "$ARTIFACTS_DIR"
    : > SHA256SUMS
    for artifact in *; do
        if [ "$artifact" != "SHA256SUMS" ]; then
            if command -v shasum > /dev/null 2>&1; then
                shasum -a 256 "$artifact" >> SHA256SUMS
            else
                sha256sum "$artifact" >> SHA256SUMS
            fi
        fi
    done
)

if [ "$BUILD_ONLY" = true ]; then
    echo "Build-only completed successfully. Artifacts are available in $ARTIFACTS_DIR."
    exit 0
fi

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
if ! gh release create "$TAG" "$ARTIFACTS_DIR"/* --title "$TAG" --notes "$NOTES"; then
    echo "Failed to create release, removing tag..."
    git push origin --delete "$TAG"
    git tag -d "$TAG"
    exit 1
fi

echo "Release $TAG published successfully."
