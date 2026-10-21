// Run with Node.js and sharp installed. Generated TGAs are committed so normal
// desktop/mobile builds need neither dependency.
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');

(async () => {
    const atlas = path.resolve(__dirname, '../FamiStudio/Resources/Atlas');
    for (const scale of [1, 2]) {
        const size = 32 * scale;
        const rgba = await sharp(path.join(atlas, 'SaveAs.svg'), { density: 72 * scale })
            .resize(size, size).ensureAlpha().raw().toBuffer();
        const header = Buffer.alloc(18);
        header[2] = 2; // Uncompressed true-color TGA.
        header.writeUInt16LE(size, 12);
        header.writeUInt16LE(size, 14);
        header[16] = 32;
        header[17] = 0x28; // Top-left origin, eight alpha bits.
        for (let i = 0; i < rgba.length; i += 4) {
            const red = rgba[i]; rgba[i] = rgba[i + 2]; rgba[i + 2] = red;
        }
        fs.writeFileSync(path.join(atlas, `SaveAs${scale === 1 ? '' : '@2x'}.tga`), Buffer.concat([header, rgba]));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
