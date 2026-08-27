import { copyFileSync, cpSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { arch, platform } from 'node:process';
import { resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { generateLicenseRtf } from './generate-license-rtf.mjs';

const targets = {
    'darwin-arm64': { rid: 'osx-arm64', triple: 'aarch64-apple-darwin', extension: '' },
    'darwin-x64': { rid: 'osx-x64', triple: 'x86_64-apple-darwin', extension: '' },
    'win32-x64': { rid: 'win-x64', triple: 'x86_64-pc-windows-msvc', extension: '.exe' }
};

const requestedRid = process.argv[2];
const requestedVersion = process.argv[3];
const target = requestedRid
    ? Object.values(targets).find(candidate => candidate.rid === requestedRid)
    : targets[`${platform}-${arch}`];
if (!target) {
    throw new Error(`Unsupported desktop target: ${requestedRid || `${platform}-${arch}`}`);
}

const desktopRoot = resolve(import.meta.dirname, '..');
const repositoryRoot = resolve(desktopRoot, '..');
const publishDir = resolve(desktopRoot, 'src-tauri', 'target', 'sidecar', target.rid);
const binariesDir = resolve(desktopRoot, 'src-tauri', 'binaries');
const resourcesWebRoot = resolve(desktopRoot, 'src-tauri', 'resources', 'wwwroot');
const projectPath = resolve(repositoryRoot, 'RemuxForge.Web', 'RemuxForge.Web.csproj');
const licenseRtfPath = resolve(desktopRoot, 'src-tauri', 'LICENSE.rtf');
rmSync(publishDir, { recursive: true, force: true });
mkdirSync(publishDir, { recursive: true });
mkdirSync(binariesDir, { recursive: true });
rmSync(resourcesWebRoot, { recursive: true, force: true });

const publishArguments = [
    'publish', projectPath,
    '-c', 'Release',
    '-r', target.rid,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeAllContentForSelfExtract=true',
    '-p:DebugType=None',
    '-o', publishDir
];
if (requestedVersion) {
    publishArguments.push(`-p:Version=${requestedVersion}`);
}

const publish = spawnSync('dotnet', publishArguments, { stdio: 'inherit' });

if (publish.status !== 0) {
    process.exit(publish.status ?? 1);
}

const publishedExecutable = resolve(publishDir, `RemuxForge.Web${target.extension}`);
if (!existsSync(publishedExecutable)) {
    throw new Error(`Published sidecar not found: ${publishedExecutable}`);
}

const bundledExecutable = resolve(binariesDir, `remuxforge-web-${target.triple}${target.extension}`);
copyFileSync(publishedExecutable, bundledExecutable);
cpSync(resolve(publishDir, 'wwwroot'), resourcesWebRoot, { recursive: true });
generateLicenseRtf(repositoryRoot, licenseRtfPath);
