// Pure rules for which artwork a tile shows. A skin names the artwork its tiles want with the CSS variable --tile-art (square, cover or
// hero), set next to the tile's aspect-ratio (on the tile, its row or :root); art.js reads it per tile, again on every skin change,
// and applies the answer, so every skin gets the same one.

/** The artwork slots a tile can ask for, each with the shape it is made for (width / height). */
export const TILE_ART_RATIO = { square: 1, cover: 2 / 3, hero: 2 };

/** The tile art a skin asks for: "square", "cover" or "hero"; anything else (a missing or odd value) is the kind's own slot. */
export function tileArtMode(cssValue, kind = 'cover') {
  const value = String(cssValue ?? '').trim().replace(/^["']|["']$/g, '').toLowerCase();
  if (Object.hasOwn(TILE_ART_RATIO, value)) return value;
  return Object.hasOwn(TILE_ART_RATIO, kind) ? kind : 'cover';
}

/** Order of the slots per wanted art: the wanted one first, then the stand-ins that look best in that shape. */
const ORDER = { square: ['square', 'cover'], cover: ['cover', 'square'], hero: ['hero', 'cover', 'square'] };

const urlsOf = (item, slot) => (item?.art?.[slot] ?? []).filter((u) => typeof u === 'string' && u);

/**
 * What a tile that wants `mode` shows: the URLs to try in order and, for the one that loaded, the slot it came from. A square tile
 * without a square picture falls back to the cover, a cover tile without a cover to the square, a hero tile to the cover, then the square.
 */
export function tileArt(item, mode) {
  const order = Object.hasOwn(ORDER, mode) ? ORDER[mode] : ORDER.cover;
  const urls = [];
  const slots = new Map();
  for (const slot of order) {
    for (const url of urlsOf(item, slot)) {
      if (slots.has(url)) continue;
      slots.set(url, slot);
      urls.push(url);
    }
  }
  return { urls, mode: order[0], slotOf: (url) => slots.get(url) ?? null };
}

/** The tile's shape from its computed aspect-ratio ("1", "2 / 3", "auto 16 / 9"); null when it has none. */
export function parseAspectRatio(cssValue) {
  const m = String(cssValue ?? '').match(/(\d*\.?\d+)\s*(?:\/\s*(\d*\.?\d+))?\s*$/);
  if (!m) return null;
  const ratio = Number(m[1]) / (m[2] === undefined ? 1 : Number(m[2]));
  return Number.isFinite(ratio) && ratio > 0 ? ratio : null;
}

/** How far a picture may differ from its tile's shape and still fill it (the crop is then at most about a quarter of a side). */
export const FILL_TOLERANCE = 1.3;

/**
 * "fill" (the picture covers the tile, centred) or "fit" (the whole picture in the middle, a blurred copy of it behind; never a stretch).
 * A square or hero picture in a tile that asked for it is made to be cut to its tile and always fills; everything else (a cover, any
 * stand-in) fills only when its shape is close to the tile's. `tileRatio` is the tile's width / height (its aspect-ratio), or null
 * when unknown: then the shape of the wanted slot counts. Unknown picture size: fill.
 */
export function tileFit({ mode, slot, imageRatio, tileRatio }) {
  if (slot === mode && mode !== 'cover') return 'fill';
  const box = tileRatio > 0 ? tileRatio : Object.hasOwn(TILE_ART_RATIO, mode) ? TILE_ART_RATIO[mode] : TILE_ART_RATIO.cover;
  if (!(imageRatio > 0)) return 'fill';
  const off = imageRatio > box ? imageRatio / box : box / imageRatio;
  return off <= FILL_TOLERANCE ? 'fill' : 'fit';
}
