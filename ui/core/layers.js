// The top layer: what stands over the tab bar, and when. The bar is a popover shown in the top layer (core/tab-bar.js), so nothing in the
// page, not a fixed layer with z-index 9999 and not a pseudo element of body, can paint over it. Only what covers the whole screen by design
// may stand over it, and only while the app says it is open: the boot screen, the screensaver, the launch screen, the web page's full screen
// mark (its own shadow root, views/browser.js) and the mouse pointer (its own shadow root, components/mouse.js). They are popovers shown after
// the bar, in the order of their old z-index. The guide menu, the dialogs, the toasts and the notification cards are NOT among them: they stand
// under the bar, so no rule of a skin for their box, children, pseudo elements or transforms can ever reach the tabs (the guide menu starts below
// the bar, the bar dims while one is open). That closes the whole class structurally instead of pinning properties one by one. Pure rules at the
// top (tested); the rest is DOM.

/** The overlays that may stand over the bar: id -> the order they stack in (the z-index they had before the bar moved to the top layer). */
export const LAYER_RANK = Object.freeze({
  cursor: 100, screensaver: 101, launch: 150, boot: 200, 'web-fullscreen-badge': 2147483000
});

/** Overlays that leave the top layer the moment they close (no fade or slide to wait for). */
export const NO_LINGER = Object.freeze(['cursor']);

/** How long a closed overlay stays in the top layer (ms): its fade or slide out. */
export const LAYER_LINGER_MS = 800;

/**
 * Whether the app has this overlay open (state read from the node): the only time it may stand over the bar. `body` is document.body.
 * Overlays that are made when needed and removed afterwards (screensaver, launch screen, the full screen mark) are open while they exist.
 */
export function overlayIsOpen(id, node, body) {
  if (!node?.isConnected) return false;
  switch (id) {
    case 'boot': return !node.classList.contains('done');
    // The pointer: only while the mouse is in use and the HUD draws it (not with the real Windows pointer).
    case 'cursor': return body.classList.contains('mouse-active') && !node.hasAttribute('data-off');
    default: return true;
  }
}

/** The ids in the order they have to be shown (lowest rank first). */
export const layerOrder = () => Object.keys(LAYER_RANK).sort((a, b) => LAYER_RANK[a] - LAYER_RANK[b]);

/** Whether this browser has popovers (WebView2 / Edge 114 and later); without, the bar falls back to a fixed layer and the overlays stay as they are. */
export const hasPopover = () => typeof HTMLElement === 'function' && typeof HTMLElement.prototype.showPopover === 'function';

const tracked = new Map(); // id -> { node, timer }
let watching = false;
let observer = null;

const isUp = (node) => { try { return node.matches(':popover-open'); } catch { return false; } };
const up = (node) => { try { node.showPopover(); } catch { /* not connected or already open */ } };
const down = (node) => { try { node.hidePopover(); } catch { /* not open */ } };

function adopt(id, node) {
  const known = tracked.get(id);
  if (known?.node === node) return;
  if (known) clearTimeout(known.timer);
  tracked.set(id, { node, timer: 0 });
  node.setAttribute('popover', 'manual');
  observer?.observe(node, { attributes: true, attributeFilter: ['hidden', 'class', 'data-off'] });
}

/** Opens and closes the overlays in the top layer to match the app's state; called when something changed (and cheap enough to call often). */
export function syncLayers() {
  if (!hasPopover()) return;
  for (const id of layerOrder()) {
    const node = document.getElementById(id);
    if (node) adopt(id, node);
  }
  let raise = false;
  for (const id of layerOrder()) {
    const entry = tracked.get(id);
    if (!entry) continue;
    const { node } = entry;
    const open = overlayIsOpen(id, node, document.body);
    if (open) {
      clearTimeout(entry.timer);
      entry.timer = 0;
      // Opened now, or a lower one opened before: everything above it is shown again after it, so the order stays that of the ranks.
      if (!isUp(node) || raise) {
        if (isUp(node)) down(node);
        up(node);
        raise = true;
      }
    } else if (isUp(node) && NO_LINGER.includes(id)) {
      down(node);
    } else if (isUp(node) && !entry.timer) {
      // A shut overlay is still fading or sliding out: it leaves the top layer when that is over, if it is still shut by then.
      entry.timer = setTimeout(() => {
        entry.timer = 0;
        if (!overlayIsOpen(id, node, document.body) && isUp(node)) down(node);
      }, LAYER_LINGER_MS);
    }
  }
}

/** The bar was (re)shown: every open overlay goes on top of it again. */
export function raiseOverlays() {
  for (const id of layerOrder()) {
    const node = tracked.get(id)?.node;
    if (node && isUp(node)) { down(node); up(node); }
  }
}

/** Starts watching: overlays that are made later, the quick menu's body class, and the overlays' own attributes. Call once. */
export function watchLayers() {
  if (watching) return;
  watching = true;
  syncLayers();
  if (!hasPopover() || typeof MutationObserver !== 'function') return;
  observer = new MutationObserver(() => syncLayers());
  for (const { node } of tracked.values()) observer.observe(node, { attributes: true, attributeFilter: ['hidden', 'class', 'data-off'] });
  observer.observe(document.body, { attributes: true, attributeFilter: ['class'], childList: true });
  syncLayers();
}

/** For tests: the ids that are in the top layer now, lowest rank first. */
export const layersUp = () => layerOrder().filter((id) => { const n = tracked.get(id)?.node; return !!n && isUp(n); });
