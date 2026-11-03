// A minimal PNG reader for the screenshot checks (8-bit RGB or RGBA, no interlace: what Edge's Page.captureScreenshot writes), and the
// "ink" measure used by scripts/ui-shot.mjs: how much of a rectangle differs from its most common colour, i.e. whether letters are drawn.
import { inflateSync } from 'node:zlib';

/** @returns {{ width: number, height: number, data: Uint8Array }} RGBA, 4 bytes per pixel. */
export function decodePng(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
  let pos = 8;
  let width = 0;
  let height = 0;
  let depth = 0;
  let type = 0;
  let interlace = 0;
  const idat = [];
  while (pos < buf.length) {
    const len = buf.readUInt32BE(pos);
    const name = buf.toString('latin1', pos + 4, pos + 8);
    const body = buf.subarray(pos + 8, pos + 8 + len);
    if (name === 'IHDR') {
      width = body.readUInt32BE(0);
      height = body.readUInt32BE(4);
      depth = body[8];
      type = body[9];
      interlace = body[12];
    } else if (name === 'IDAT') idat.push(body);
    else if (name === 'IEND') break;
    pos += 12 + len;
  }
  if (depth !== 8 || ![2, 6].includes(type) || interlace) throw new Error(`unsupported PNG (depth ${depth}, type ${type}, interlace ${interlace})`);
  const bpp = type === 6 ? 4 : 3;
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * bpp;
  const out = new Uint8Array(width * height * 4);
  let prev = new Uint8Array(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Uint8Array.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let i = 0; i < stride; i++) {
      const a = i >= bpp ? line[i - bpp] : 0;
      const b = prev[i];
      const c = i >= bpp ? prev[i - bpp] : 0;
      let add = 0;
      if (filter === 1) add = a;
      else if (filter === 2) add = b;
      else if (filter === 3) add = (a + b) >> 1;
      else if (filter === 4) {
        const p = a + b - c;
        const pa = Math.abs(p - a);
        const pb = Math.abs(p - b);
        const pc = Math.abs(p - c);
        add = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
      }
      line[i] = (line[i] + add) & 255;
    }
    for (let x = 0; x < width; x++) {
      const o = (y * width + x) * 4;
      out[o] = line[x * bpp];
      out[o + 1] = line[x * bpp + 1];
      out[o + 2] = line[x * bpp + 2];
      out[o + 3] = bpp === 4 ? line[x * bpp + 3] : 255;
    }
    prev = line;
  }
  return { width, height, data: out };
}

/**
 * The share (0..1) of the pixels in a rectangle that differ from the rectangle's most common colour by more than `threshold` (the sum of
 * the three colour differences). Plain letters on a plain ground give a few percent; a rectangle painted over, or letters in the colour of
 * their ground, give 0.
 */
export function ink(png, { x, y, w, h }, threshold = 60) {
  const x0 = Math.max(0, Math.floor(x));
  const y0 = Math.max(0, Math.floor(y));
  const x1 = Math.min(png.width, Math.ceil(x + w));
  const y1 = Math.min(png.height, Math.ceil(y + h));
  const counts = new Map();
  for (let j = y0; j < y1; j++) {
    for (let i = x0; i < x1; i++) {
      const o = (j * png.width + i) * 4;
      // Quantised, so a soft gradient behind the letters is one ground.
      const key = ((png.data[o] >> 4) << 8) | ((png.data[o + 1] >> 4) << 4) | (png.data[o + 2] >> 4);
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
  }
  let modal = 0;
  let best = -1;
  for (const [k, n] of counts) if (n > best) { best = n; modal = k; }
  const [mr, mg, mb] = [((modal >> 8) & 15) * 16 + 8, ((modal >> 4) & 15) * 16 + 8, (modal & 15) * 16 + 8];
  let differ = 0;
  let total = 0;
  for (let j = y0; j < y1; j++) {
    for (let i = x0; i < x1; i++) {
      const o = (j * png.width + i) * 4;
      total++;
      if (Math.abs(png.data[o] - mr) + Math.abs(png.data[o + 1] - mg) + Math.abs(png.data[o + 2] - mb) > threshold) differ++;
    }
  }
  return total ? differ / total : 0;
}

const lin = (v) => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; };
const lum = (r, g, b) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);

/**
 * The contrast ratio between a rectangle's ground (its most common colour) and the pixel that stands out most from it (the strongest ink of the
 * letters): the letters as really painted against what lies behind them, whatever the page and the skin did. 1 when nothing differs.
 */
export function inkContrast(png, { x, y, w, h }) {
  const x0 = Math.max(0, Math.floor(x));
  const y0 = Math.max(0, Math.floor(y));
  const x1 = Math.min(png.width, Math.ceil(x + w));
  const y1 = Math.min(png.height, Math.ceil(y + h));
  const counts = new Map();
  for (let j = y0; j < y1; j++) {
    for (let i = x0; i < x1; i++) {
      const o = (j * png.width + i) * 4;
      const key = ((png.data[o] >> 4) << 8) | ((png.data[o + 1] >> 4) << 4) | (png.data[o + 2] >> 4);
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
  }
  let modal = 0;
  let best = -1;
  for (const [k, n] of counts) if (n > best) { best = n; modal = k; }
  const ground = lum(((modal >> 8) & 15) * 16 + 8, ((modal >> 4) & 15) * 16 + 8, (modal & 15) * 16 + 8);
  let top = 1;
  for (let j = y0; j < y1; j++) {
    for (let i = x0; i < x1; i++) {
      const o = (j * png.width + i) * 4;
      const l = lum(png.data[o], png.data[o + 1], png.data[o + 2]);
      const ratio = (Math.max(l, ground) + 0.05) / (Math.min(l, ground) + 0.05);
      if (ratio > top) top = ratio;
    }
  }
  return top;
}
