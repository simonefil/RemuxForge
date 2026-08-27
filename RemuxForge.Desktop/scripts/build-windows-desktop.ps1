param(
    [Parameter(Mandatory = $true)]
    [string]$SourceArchive,

    [Parameter(Mandatory = $true)]
    [string]$WorkDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

if (Test-Path -LiteralPath $WorkDirectory) {
    Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $ArtifactsDirectory) {
    Remove-Item -LiteralPath $ArtifactsDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $ArtifactsDirectory | Out-Null
Expand-Archive -LiteralPath $SourceArchive -DestinationPath $WorkDirectory -Force

$desktopDirectory = Join-Path $WorkDirectory "RemuxForge.Desktop"
$tauriConfigPath = Join-Path $WorkDirectory "tauri.release.conf.json"
@{ version = $Version } | ConvertTo-Json | Set-Content -LiteralPath $tauriConfigPath -Encoding UTF8

Push-Location $desktopDirectory
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed with exit code $LASTEXITCODE"
    }

    & npm run prepare-sidecar -- win-x64 $Version
    if ($LASTEXITCODE -ne 0) {
        throw "Windows sidecar build failed with exit code $LASTEXITCODE"
    }

    & npx tauri build --target x86_64-pc-windows-msvc --bundles msi --config $tauriConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw "Tauri Windows MSI build failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$bundleDirectory = Join-Path $desktopDirectory "src-tauri/target/x86_64-pc-windows-msvc/release/bundle/msi"
$installers = @(Get-ChildItem -LiteralPath $bundleDirectory -File -Filter "*.msi")
if ($installers.Count -ne 1) {
    throw "Expected one MSI installer in $bundleDirectory, found $($installers.Count)"
}

Copy-Item -LiteralPath $installers[0].FullName -Destination (Join-Path $ArtifactsDirectory "RemuxForge-Desktop-win-x64.msi") -Force
