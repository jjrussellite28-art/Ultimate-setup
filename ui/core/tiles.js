// Tiles and what each one shows (art.js wires it to the page; tested with fake boxes). A tile reads its skin's --tile-art and
// aspect-ratio once it is in the page and again on every skin change, then shows the artwork the rules in square-rules.js give.
// Reads and writes are batched: all tiles of a pass are read first, then written, so a page of tiles costs one or two style
// recalculations, not one per tile.

import { TILE_ART_RATIO, parseAspectRatio, tileArt, tileArtMode, tileFit } from './square-rules.js';

/**
 * deps.read(box) -> { tileArt, aspectRatio, width, height } (the computed --tile-art and aspect-ratio, the box's size; reads only);
 * deps.load(urls, (url, img) => …) loads the first URL that works; deps.frame(fn) runs fn in a later frame; deps.cssUrl(url).
 */
export function createTiles({ read, load, frame, cssUrl, later = queueMicrotask }) {
  const tiles = new WeakMap();
  let pending = [];
  let scheduled = false;
  // Tiles that were not in the page after a few frames: they show their kind's own art and are asked again on later passes.
  const provisional = new Set();

  const measure = (box, kind) => {
    const css = read(box);
    const ratio = parseAspectRatio(css.aspectRatio) ?? (css.height > 0 ? css.width / css.height : null);
    return { mode: tileArtMode(css.tileArt, kind), ratio };
  };
  const readRatio = (box, kind) => measure(box, kind).ratio;

  /** Writes only: the box shows `mode` from now on (classes, data-tile-art) and its load starts; a load for the art before is ignored. */
  function show(box, s, mode) {
    const token = ++s.token;
    s.mode = mode;
    s.url = s.slot = s.imageRatio = null;
    box.dataset.tileArt = mode;
    box.classList.toggle('art-square', mode === 'square');
    box.classList.remove('loaded', 'art-fit', 'art-wide');
    box.style.backgroundImage = '';
    box.style.removeProperty('--art-img');
    const wanted = tileArt(s.item, mode);
    load(wanted.urls, (url, img) => {
      if (token !== s.token) return;
      s.url = url;
      s.slot = wanted.slotOf(url);
      s.imageRatio = img?.naturalWidth > 0 && img.naturalHeight > 0 ? img.naturalWidth / img.naturalHeight : null;
      box.style.backgroundImage = cssUrl(url);
      box.style.setProperty('--art-img', cssUrl(url));
      box.classList.add('loaded');
      fit(box, s);
    });
  }

  /** Writes only: fill the tile, or the whole picture over a blurred copy (art-fit; art-wide too when the picture is the wider one). */
  function fit(box, s) {
    if (!s.url) return;
    const fitted = tileFit({ mode: s.mode, slot: s.slot, imageRatio: s.imageRatio, tileRatio: s.ratio }) === 'fit';
    box.classList.toggle('art-fit', fitted);
    box.classList.toggle('art-wide', fitted && s.imageRatio > (s.ratio ?? TILE_ART_RATIO[s.mode]));
  }

  /**
   * One batch: read every box first, then write. A box whose art changes gets its shape read once more after all writes (a skin
   * may shape .art-square tiles), before any picture can have loaded. A failing tile does not stop the others.
   */
  function apply(entries) {
    const answers = [];
    for (const [box, s] of entries) {
      try {
        answers.push([box, s, measure(box, s.kind)]);
      } catch (e) {
        console.error(e);
      }
    }
    const changed = [];
    for (const [box, s, { mode, ratio }] of answers) {
      try {
        s.ratio = ratio;
        if (mode !== s.mode) {
          show(box, s, mode);
          changed.push([box, s]);
        } else fit(box, s);
      } catch (e) {
        console.error(e);
      }
    }
    for (const [box, s] of changed) {
      try {
        s.ratio = readRatio(box, s.kind);
      } catch (e) {
        console.error(e);
      }
    }
  }

  function schedule(nextFrame) {
    if (scheduled) return;
    scheduled = true;
    (nextFrame ? frame : later)(flush);
  }

  function flush() {
    scheduled = false;
    const batch = pending;
    pending = [];
    const ready = [];
    for (const { box, tries } of batch) {
      const s = tiles.get(box);
      if (!s || (s.mode !== null && !s.provisional)) continue;
      if (box.isConnected) ready.push([box, s]);
      else if (tries < 3) pending.push({ box, tries: tries + 1 });
      else if (s.mode === null) {
        // Still not in the page: its kind's own art for now, and asked again once it is.
        s.provisional = true;
        provisional.add(new WeakRef(box));
        show(box, s, tileArtMode('', s.kind));
      }
    }
    for (const ref of provisional) {
      const box = ref.deref();
      if (!box) provisional.delete(ref);
      else if (box.isConnected) {
        provisional.delete(ref);
        const s = tiles.get(box);
        if (s && !ready.some(([b]) => b === box)) ready.push([box, s]);
      }
    }
    for (const [, s] of ready) s.provisional = false;
    apply(ready);
    if (pending.length) schedule(true);
  }

  return {
    /** Marks the box as a tile of `item` ("cover" or "hero" art by kind); what it shows is decided once it is in the page. */
    add(box, item, kind) {
      tiles.set(box, { item, kind, mode: null, ratio: null, token: 0, slot: null, imageRatio: null, url: null, provisional: false });
      // Set at once: a skin can tell the tiles this app chooses for from those of older versions (data-tile-art is the art they show).
      box.dataset.tileArt = tileArtMode('', kind);
      pending.push({ box, tries: 0 });
      schedule(false);
      return box;
    },
    /** The skin changed: every tile given asks again and switches its artwork in place when the answer changed. */
    refresh(boxes) {
      const entries = [];
      for (const box of boxes) {
        const s = tiles.get(box);
        if (s && box.isConnected && s.mode !== null) entries.push([box, s]);
      }
      for (const [, s] of entries) s.provisional = false;
      apply(entries);
    },
    /** For tests: what a box shows now. */
    stateOf: (box) => tiles.get(box)
  };
}
