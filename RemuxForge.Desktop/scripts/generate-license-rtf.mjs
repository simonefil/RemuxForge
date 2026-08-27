import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { readFileSync, writeFileSync } from 'node:fs';

function escapeRtf(text) {
    return Array.from(text, character => {
        if (character === '\\' || character === '{' || character === '}') {
            return `\\${character}`;
        }

        const codePoint = character.codePointAt(0);
        if (codePoint >= 0x20 && codePoint <= 0x7e) {
            return character;
        }

        const signedCodePoint = codePoint > 0x7fff ? codePoint - 0x10000 : codePoint;
        return `\\u${signedCodePoint}?`;
    }).join('');
}

export function generateLicenseRtf(repositoryRoot, outputPath) {
    const licenseText = readFileSync(resolve(repositoryRoot, 'LICENSE'), 'utf8')
        .replace(/\r\n/g, '\n')
        .replace(/\n$/, '');
    const paragraphs = licenseText
        .split('\n')
        .map(line => `${escapeRtf(line.trimStart())}\\par`)
        .join('\n');
    const rtf = `{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0\\fnil\\fcharset0 Segoe UI;}}\n`
        + `\\viewkind4\\uc1\\pard\\li0\\ri0\\sa0\\sb0\\sl220\\slmult1\\f0\\fs18\n`
        + `${paragraphs}\n}`;

    writeFileSync(outputPath, rtf, 'ascii');
}

const scriptPath = fileURLToPath(import.meta.url);
if (process.argv[1] === scriptPath) {
    const desktopRoot = resolve(dirname(scriptPath), '..');
    generateLicenseRtf(resolve(desktopRoot, '..'), resolve(desktopRoot, 'src-tauri', 'LICENSE.rtf'));
}
