// Preview only (?demo): made-up games with generated artwork instead of real store covers, for the
// README screenshots and the demo GIF. No network, no third-party images. Imported by mock-api.js;
// never loaded inside the app.

export const demoMode = new URLSearchParams(location.search).has('demo');

const svgUrl = (w, h, body) => 'data:image/svg+xml,' + encodeURIComponent(
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" width="${w}" height="${h}">${body}</svg>`);

// Small deterministic random numbers, so every run draws the same pictures.
const rng = (seed) => () => ((seed = (seed * 16807) % 2147483647) - 1) / 2147483646;

/** One scene per game: sky, a motif and the ground, drawn for any width and height. */
function scene(g, w, h, seed = 1) {
  const r = rng(g.seed * 97 + seed);
  const [a, b] = g.hues;
  const horizon = h * (g.horizon ?? 0.66);
  const unit = Math.min(w, h);
  let s = `<defs><linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="hsl(${a} ${g.sat ?? 60}% 12%)"/>`
    + `<stop offset=".7" stop-color="hsl(${b} ${g.sat ?? 70}% ${g.glow ?? 42}%)"/><stop offset="1" stop-color="hsl(${b + 15} 80% ${(g.glow ?? 42) + 14}%)"/></linearGradient>`
    + `<radialGradient id="sun" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="hsl(${b + 20} 100% 88%)"/><stop offset=".55" stop-color="hsl(${b + 10} 95% 66%)"/><stop offset="1" stop-color="hsl(${b} 90% 55%)" stop-opacity="0"/></radialGradient>`
    + `<linearGradient id="ground" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="hsl(${a} 50% 10%)"/><stop offset="1" stop-color="hsl(${a} 40% 4%)"/></linearGradient></defs>`
    + `<rect width="${w}" height="${h}" fill="url(#sky)"/>`;
  const stars = (n) => { for (let i = 0; i < n; i++) s += `<circle cx="${(r() * w).toFixed(0)}" cy="${(r() * horizon * 0.8).toFixed(0)}" r="${(r() * 1.6 + 0.4) * unit / 600}" fill="#fff" opacity="${(r() * 0.6 + 0.2).toFixed(2)}"/>`; };
  const ridge = (base, amp, light, steps = 9) => {
    let p = `M0 ${h} L0 ${base}`;
    for (let i = 1; i <= steps; i++) p += ` L${(w * i) / steps} ${base - r() * amp}`;
    s += `<path d="${p} L${w} ${h} Z" fill="hsl(${a} 45% ${light}%)"/>`;
  };
  const sunAt = (cx, cy, rad) => { s += `<circle cx="${cx}" cy="${cy}" r="${rad * 1.9}" fill="url(#sun)" opacity=".55"/><circle cx="${cx}" cy="${cy}" r="${rad}" fill="hsl(${b + 20} 100% 80%)"/>`; };
  switch (g.motif) {
    case 'synth': {
      stars(40);
      const rad = unit * 0.24;
      s += `<defs><linearGradient id="band" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="hsl(${b + 40} 100% 70%)"/><stop offset="1" stop-color="hsl(${b - 20} 100% 55%)"/></linearGradient>`
        + `<clipPath id="half"><rect x="0" y="0" width="${w}" height="${horizon}"/></clipPath></defs>`;
      s += `<g clip-path="url(#half)"><circle cx="${w / 2}" cy="${horizon - rad * 0.35}" r="${rad}" fill="url(#band)"/>`;
      for (let i = 0; i < 6; i++) s += `<rect x="0" y="${horizon - rad * 0.35 + i * rad * 0.13}" width="${w}" height="${rad * 0.03 + i * rad * 0.012}" fill="hsl(${a} 60% 14%)"/>`;
      s += `</g><rect y="${horizon}" width="${w}" height="${h - horizon}" fill="hsl(${a} 70% 7%)"/>`;
      for (let i = -12; i <= 12; i++) s += `<line x1="${w / 2 + i * w * 0.02}" y1="${horizon}" x2="${w / 2 + i * w * 0.16}" y2="${h}" stroke="hsl(${b + 30} 100% 62%)" stroke-width="${unit / 300}" opacity=".7"/>`;
      for (let i = 1; i < 9; i++) { const y = horizon + (h - horizon) * (i / 8) ** 1.8; s += `<line x1="0" y1="${y}" x2="${w}" y2="${y}" stroke="hsl(${b + 30} 100% 62%)" stroke-width="${unit / 300}" opacity=".7"/>`; }
      return s;
    }
    case 'city': {
      stars(30);
      sunAt(w * 0.72, horizon * 0.42, unit * 0.11);
      for (let layer = 0; layer < 3; layer++) {
        let x = -r() * 40;
        while (x < w) {
          const bw = unit * (0.05 + r() * 0.08), bh = h * (0.18 + r() * (0.34 - layer * 0.08));
          s += `<rect x="${x}" y="${horizon + layer * h * 0.06 - bh}" width="${bw}" height="${bh + h}" fill="hsl(${a} 45% ${16 - layer * 5}%)"/>`;
          if (layer === 2) for (let wy = horizon + layer * h * 0.06 - bh + 10; wy < h; wy += unit * 0.035) for (let wx = x + 6; wx < x + bw - 8; wx += unit * 0.03) if (r() > 0.6) s += `<rect x="${wx}" y="${wy}" width="${unit * 0.012}" height="${unit * 0.016}" fill="hsl(${b + 30} 100% 70%)" opacity=".85"/>`;
          x += bw + unit * 0.01;
        }
      }
      return s;
    }
    case 'planet': {
      stars(90);
      const cx = w * 0.62, cy = h * 0.4, rad = unit * 0.26;
      s += `<defs><linearGradient id="pl" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${b + 30} 90% 72%)"/><stop offset="1" stop-color="hsl(${a} 70% 22%)"/></linearGradient></defs>`
        + `<ellipse cx="${cx}" cy="${cy}" rx="${rad * 1.9}" ry="${rad * 0.42}" fill="none" stroke="hsl(${b + 20} 80% 75%)" stroke-width="${rad * 0.08}" opacity=".5" transform="rotate(-14 ${cx} ${cy})"/>`
        + `<circle cx="${cx}" cy="${cy}" r="${rad}" fill="url(#pl)"/>`
        + `<circle cx="${w * 0.2}" cy="${h * 0.22}" r="${rad * 0.18}" fill="hsl(${a + 30} 50% 70%)"/>`;
      ridge(h * 0.86, h * 0.06, 6, 14);
      return s;
    }
    case 'moon': {
      stars(70);
      const cx = w * 0.66, cy = h * 0.27, rad = unit * 0.13;
      s += `<circle cx="${cx}" cy="${cy}" r="${rad * 2.4}" fill="url(#sun)" opacity=".35"/><circle cx="${cx}" cy="${cy}" r="${rad}" fill="hsl(${b + 20} 60% 88%)"/><circle cx="${cx + rad * 0.4}" cy="${cy - rad * 0.2}" r="${rad * 0.9}" fill="hsl(${a} ${g.sat ?? 60}% 16%)"/>`;
      ridge(horizon, h * 0.12, 12, 7);
      for (let i = 0; i < 26; i++) {
        const x = r() * w, th = h * (0.14 + r() * 0.2), tw = th * 0.32, y = horizon + h * 0.1 + r() * h * 0.2;
        s += `<path d="M${x} ${y - th} L${x - tw} ${y} L${x + tw} ${y} Z" fill="hsl(${a} 50% ${4 + r() * 5}%)"/>`;
      }
      return s;
    }
    case 'waves': {
      stars(20);
      sunAt(w * 0.5, horizon * 0.86, unit * 0.15);
      for (let i = 0; i < 7; i++) {
        const y = horizon + i * (h - horizon) / 6, amp = unit * 0.02 * (1 + i * 0.4);
        let p = `M0 ${y}`;
        for (let x = 0; x <= w; x += w / 8) p += ` Q${x + w / 16} ${y - amp} ${x + w / 8} ${y}`;
        s += `<path d="${p} L${w} ${h} L0 ${h} Z" fill="hsl(${a} 60% ${14 - i * 1.5}%)" opacity=".95"/>`;
      }
      return s;
    }
    case 'hills': {
      sunAt(w * 0.3, horizon * 0.42, unit * 0.12);
      for (let i = 0; i < 4; i++) {
        const y = horizon + i * h * 0.07;
        s += `<ellipse cx="${w * (0.2 + r() * 0.6)}" cy="${y + h * 0.3}" rx="${w * 0.8}" ry="${h * 0.34}" fill="hsl(${g.ground ?? 110} ${45 - i * 5}% ${30 - i * 6}%)"/>`;
      }
      return s;
    }
    case 'flame': {
      sunAt(w * 0.5, horizon * 0.62, unit * 0.2);
      for (let i = 0; i < 18; i++) {
        const x = r() * w, fh = h * (0.15 + r() * 0.3), fw = fh * 0.22;
        s += `<path d="M${x - fw} ${h} Q${x - fw} ${h - fh * 0.5} ${x} ${h - fh} Q${x + fw} ${h - fh * 0.5} ${x + fw} ${h} Z" fill="hsl(${b + r() * 30} 100% ${45 + r() * 20}%)" opacity=".75"/>`;
      }
      ridge(h * 0.9, h * 0.05, 5, 12);
      return s;
    }
    case 'blocks': {
      for (let i = 0; i < 40; i++) {
        const sz = unit * (0.05 + r() * 0.14), x = r() * w, y = r() * h;
        s += `<rect x="${x}" y="${y}" width="${sz}" height="${sz}" rx="${sz * 0.18}" fill="hsl(${(a + i * 47) % 360} 85% ${50 + r() * 15}%)" opacity="${(0.5 + r() * 0.5).toFixed(2)}" transform="rotate(${(r() * 40 - 20).toFixed(0)} ${x + sz / 2} ${y + sz / 2})"/>`;
      }
      return s;
    }
    default: { // mountains
      stars(25);
      sunAt(w * 0.5, horizon * 0.62, unit * (g.ring ? 0.1 : 0.14));
      if (g.ring) s += `<circle cx="${w * 0.5}" cy="${horizon * 0.62}" r="${unit * 0.22}" fill="none" stroke="hsl(${b + 20} 100% 75%)" stroke-width="${unit * 0.012}" opacity=".8"/>`;
      ridge(horizon - h * 0.08, h * 0.22, 22, 6);
      ridge(horizon, h * 0.16, 14, 8);
      ridge(horizon + h * 0.12, h * 0.1, 7, 11);
      return s;
    }
  }
}

const esc = (t) => t.replace(/&/g, '&amp;').replace(/</g, '&lt;');

/** Splits a title into lines of about `max` characters. */
function lines(title, max) {
  const out = [];
  for (const word of title.split(' ')) {
    if (out.length && (out.at(-1) + ' ' + word).length <= max) out[out.length - 1] += ' ' + word;
    else out.push(word);
  }
  return out;
}

/** Title lines; `y` is the first baseline, or the last one with `fromBottom`. */
function titleBlock(g, w, y, size, fromBottom = false) {
  const anchor = 'middle', x = w / 2;
  const ls = lines(g.name.toUpperCase(), 11);
  const fs = Math.min(size, (w * 0.86) / Math.max(...ls.map((l) => l.length)) / 0.68);
  const y0 = fromBottom ? y - (ls.length - 1) * fs * 0.98 : y;
  let s = `<defs><linearGradient id="tt" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="hsl(${g.hues[1] + 20} 100% 82%)"/></linearGradient>`
    + `<filter id="ts" x="-10%" y="-30%" width="120%" height="160%"><feDropShadow dx="0" dy="${fs * 0.06}" stdDeviation="${fs * 0.08}" flood-color="#000" flood-opacity=".7"/></filter></defs>`;
  s += `<g filter="url(#ts)" font-family="Segoe UI, Bahnschrift, Arial, sans-serif" font-weight="900" text-anchor="${anchor}" fill="url(#tt)" letter-spacing="${fs * 0.04}">`;
  ls.forEach((l, i) => (s += `<text x="${x}" y="${y0 + i * fs * 0.98}" font-size="${fs}">${esc(l)}</text>`));
  return s + '</g>';
}

// Titles stay inside the middle square (y 150-750): some skins crop covers to square tiles.
const cover = (g) => svgUrl(600, 900, scene(g, 600, 900) + (g.titleTop ? titleBlock(g, 600, 250, 96) : titleBlock(g, 600, 715, 96, true)));
const hero = (g, seed = 1) => svgUrl(1920, 620, scene(g, 1920, 620, seed));

/** id in the mock library -> made-up game. Hues: [sky top, glow]. */
const GAMES = {
  'steam:1245620': { name: 'Ashen Crown', hues: [250, 28], motif: 'mountains', ring: true, seed: 3 },
  'steam:1091500': { name: 'Neon Courier', hues: [270, 320], motif: 'city', seed: 5 },
  'steam:1086940': { name: 'Tales of Emberfall', hues: [230, 280], motif: 'moon', seed: 7, glow: 30 },
  'steam:367520': { name: 'Hollow Lantern', hues: [215, 190], motif: 'moon', seed: 11, sat: 40, glow: 26 },
  'steam:413150': { name: 'Clover Fields', hues: [200, 40], motif: 'hills', seed: 13, glow: 62, ground: 105 },
  'steam:1145360': { name: 'Underflame', hues: [350, 15], motif: 'flame', seed: 17 },
  'steam:271590': { name: 'Skyline Rally', hues: [265, 315], motif: 'synth', seed: 19 },
  'steam:1174180': { name: 'Dust and Iron', hues: [15, 30], motif: 'mountains', seed: 23, glow: 50 },
  'steam:990080': { name: 'Starlight Academy', hues: [235, 260], motif: 'planet', seed: 29, titleTop: true },
  'steam:632360': { name: 'Orbit Drifters', hues: [200, 175], motif: 'planet', seed: 31, titleTop: true },
  'steam:504230': { name: 'Summit', hues: [285, 330], motif: 'mountains', seed: 37, titleTop: true },
  'steam:374320': { name: 'Grim Bastion', hues: [220, 10], motif: 'mountains', seed: 41, sat: 25, glow: 30 },
  'steam:292030': { name: 'Tidewater', hues: [210, 25], motif: 'waves', seed: 43 },
  'steam:1817070': { name: 'Webline City', hues: [225, 350], motif: 'city', seed: 47 },
  'epic:fortnite': { name: 'Block Party', hues: [260, 200], motif: 'blocks', seed: 53, titleTop: true }
};

const appIcon = (body, bg) => svgUrl(64, 64, `<rect width="64" height="64" rx="16" fill="${bg}"/>${body}`);
const APPS = {
  'custom:youtube': { name: 'Video Player', icon: appIcon('<path d="M25 20 L46 32 L25 44 Z" fill="#fff"/>', '#e2475b') },
  'custom:netflix': { name: 'Movie Night', icon: appIcon('<rect x="14" y="20" width="36" height="26" rx="4" fill="none" stroke="#fff" stroke-width="4"/><path d="M28 27 L38 33 L28 39 Z" fill="#fff"/>', '#7b4dff') },
  'custom:spotify': { name: 'Music', icon: appIcon('<path d="M26 44 V20 L44 16 V40" fill="none" stroke="#fff" stroke-width="4" stroke-linejoin="round"/><circle cx="22" cy="44" r="5" fill="#fff"/><circle cx="40" cy="40" r="5" fill="#fff"/>', '#1fa776') },
  'custom:kodi': { name: 'Media Center', icon: appIcon('<circle cx="32" cy="32" r="15" fill="none" stroke="#fff" stroke-width="4"/><circle cx="32" cy="32" r="5" fill="#fff"/>', '#2b8fd6') }
};

/** Swaps names and artwork of the mock library in place (ids stay, so collections still match). */
export function applyDemo(library) {
  for (const item of library) {
    const g = GAMES[item.id];
    if (g) {
      item.name = g.name;
      // No logo art: a generated logo would only repeat the name under it.
      item.art = { cover: [cover(g)], hero: [hero(g)], logo: [] };
      continue;
    }
    const app = APPS[item.id];
    if (app) {
      item.name = app.name;
      item.art = { cover: [], hero: [], logo: [], icon: [app.icon] };
    }
  }
}

