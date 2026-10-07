// UX6-01 (E-UX6-4/5): the app's icons from one drawing — the «R» monogram until the company's logo arrives (replace MARK and rerun:
// `node scripts/icons.mjs`). Writes the Android icons (any and maskable), the iPhone icon and the browser tab icon.
import { writeFile } from "node:fs/promises";
import sharp from "sharp";

const BLUE = "#0b5cad";
// The «R» on a 512 grid, drawn with strokes so no typeface is needed.
const MARK = `<path d="M184 384V128h92a76 76 0 0 1 0 152h-92M268 280l62 104" fill="none" stroke="#fff" stroke-width="54" stroke-linecap="round" stroke-linejoin="round"/>`;

/** `radius` rounds the tile (0 = full bleed for maskable / iPhone); `scale` shrinks the mark into the maskable safe zone. */
function svg({ radius, scale }) {
  const offset = (512 * (1 - scale)) / 2;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512"><rect width="512" height="512" rx="${radius}" fill="${BLUE}"/><g transform="translate(${offset} ${offset}) scale(${scale})">${MARK}</g></svg>`;
}

const png = (markup, size, path) => sharp(Buffer.from(markup)).resize(size, size).png().toFile(path);

await png(svg({ radius: 112, scale: 1 }), 192, "public/icons/icon-192.png");
await png(svg({ radius: 112, scale: 1 }), 512, "public/icons/icon-512.png");
await png(svg({ radius: 0, scale: 0.78 }), 512, "public/icons/maskable-512.png");
await png(svg({ radius: 0, scale: 0.86 }), 180, "src/app/apple-icon.png");
await writeFile("src/app/icon.svg", svg({ radius: 112, scale: 1 }));
console.log("icons written");
