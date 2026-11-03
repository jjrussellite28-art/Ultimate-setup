// The phone remote's encrypted channel ("seal v1"), the phone's half. The page is served over plain HTTP in the home network, where the
// browser gives it no WebCrypto (that needs a secure context), so this file carries its own SHA-256 and HMAC-SHA256 and nothing else.
// Format and derivations are the PC's (src/CouchHud.Core/Remote/RemoteSeal.cs); both are checked against the same test vectors
// (tests RemoteSealTests, scripts/ui-logic-check.mjs).
//
//   message = "1." + seq + "." + base64url(nonce16 | ciphertext | tag32)
//   encKey  = HMAC(key, "couchhud-remote-v1 enc"), macKey = HMAC(key, "couchhud-remote-v1 mac")
//   block i = HMAC(encKey, nonce | u32be(i)), ciphertext = plaintext XOR blocks
//   tag     = HMAC(macKey, dir | u64be(seq) | u32be(len(aad)) | aad | nonce | ciphertext)

export const DIR = Object.freeze({ request: 1, response: 2, event: 3 });

const K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
  0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
  0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
  0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
]);

/** SHA-256 of bytes (Uint8Array) as 32 bytes. */
export function sha256(data) {
  const h = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
  const bitLen = data.length * 8;
  const total = Math.ceil((data.length + 9) / 64) * 64;
  const m = new Uint8Array(total);
  m.set(data);
  m[data.length] = 0x80;
  const view = new DataView(m.buffer);
  view.setUint32(total - 8, Math.floor(bitLen / 0x100000000));
  view.setUint32(total - 4, bitLen >>> 0);
  const w = new Uint32Array(64);
  for (let off = 0; off < total; off += 64) {
    for (let i = 0; i < 16; i++) w[i] = view.getUint32(off + i * 4);
    for (let i = 16; i < 64; i++) {
      const a = w[i - 15], b = w[i - 2];
      const s0 = ((a >>> 7) | (a << 25)) ^ ((a >>> 18) | (a << 14)) ^ (a >>> 3);
      const s1 = ((b >>> 17) | (b << 15)) ^ ((b >>> 19) | (b << 13)) ^ (b >>> 10);
      w[i] = (w[i - 16] + s0 + w[i - 7] + s1) >>> 0;
    }
    let [a, b, c, d, e, f, g, hh] = h;
    for (let i = 0; i < 64; i++) {
      const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
      const ch = (e & f) ^ (~e & g);
      const t1 = (hh + S1 + ch + K[i] + w[i]) >>> 0;
      const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const t2 = (S0 + maj) >>> 0;
      hh = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
    }
    h[0] = (h[0] + a) >>> 0; h[1] = (h[1] + b) >>> 0; h[2] = (h[2] + c) >>> 0; h[3] = (h[3] + d) >>> 0;
    h[4] = (h[4] + e) >>> 0; h[5] = (h[5] + f) >>> 0; h[6] = (h[6] + g) >>> 0; h[7] = (h[7] + hh) >>> 0;
  }
  const out = new Uint8Array(32);
  const ov = new DataView(out.buffer);
  for (let i = 0; i < 8; i++) ov.setUint32(i * 4, h[i]);
  return out;
}

/** HMAC-SHA256(key, data), both Uint8Array. */
export function hmac(key, data) {
  let k = key.length > 64 ? sha256(key) : key;
  const pad = new Uint8Array(64);
  pad.set(k);
  const inner = new Uint8Array(64 + data.length);
  const outer = new Uint8Array(64 + 32);
  for (let i = 0; i < 64; i++) { inner[i] = pad[i] ^ 0x36; outer[i] = pad[i] ^ 0x5c; }
  inner.set(data, 64);
  outer.set(sha256(inner), 64);
  return sha256(outer);
}

export const utf8 = (text) => new TextEncoder().encode(text);
const concat = (...parts) => {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
};

export function b64url(bytes) {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

export function fromB64url(text) {
  if (typeof text !== 'string' || !/^[A-Za-z0-9_-]*$/.test(text) || text.length % 4 === 1) return null;
  const s = atob(text.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((text.length + 3) % 4));
  const out = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
  return out;
}

export const hex = (bytes) => Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');

/** HMAC(key, "couchhud-remote-v1 " + label | parts...). */
export function derive(key, label, ...parts) {
  return hmac(key, concat(utf8(`couchhud-remote-v1 ${label}`), ...parts));
}

export const pairKey = (secret) => derive(secret, 'pair');
export const sessionKey = (deviceKey, clientNonce, serverNonce) => derive(deviceKey, 'session', clientNonce, serverNonce);

/** The proof for a URL (event stream, image): 32 hex characters, as RemoteSeal.UrlProof. */
export function urlProof(key, purpose, value) {
  return hex(hmac(derive(key, 'mac'), utf8(`${purpose}|${value}`))).slice(0, 32);
}

function keystream(encKey, nonce, data) {
  const out = new Uint8Array(data.length);
  const input = new Uint8Array(nonce.length + 4);
  input.set(nonce);
  const view = new DataView(input.buffer);
  for (let i = 0; i * 32 < data.length; i++) {
    view.setUint32(nonce.length, i);
    const block = hmac(encKey, input);
    for (let j = 0; j < 32 && i * 32 + j < data.length; j++) out[i * 32 + j] = data[i * 32 + j] ^ block[j];
  }
  return out;
}

function tag(macKey, dir, seq, aad, nonce, ct) {
  const a = utf8(aad);
  const head = new Uint8Array(13);
  const view = new DataView(head.buffer);
  head[0] = dir;
  view.setUint32(1, Math.floor(seq / 0x100000000));
  view.setUint32(5, seq >>> 0);
  view.setUint32(9, a.length);
  return hmac(macKey, concat(head, a, nonce, ct));
}

function equal(a, b) {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

/** Random bytes from the browser's generator (available without a secure context). */
export function randomBytes(n) {
  return crypto.getRandomValues(new Uint8Array(n));
}

/** Seals a text (or bytes); nonce only for the test vectors. */
export function seal(key, dir, seq, aad, plaintext, nonce = randomBytes(16)) {
  if (!Number.isSafeInteger(seq) || seq < 0) throw new Error('seq');
  const pt = typeof plaintext === 'string' ? utf8(plaintext) : plaintext;
  const ct = keystream(derive(key, 'enc'), nonce, pt);
  return `1.${seq}.${b64url(concat(nonce, ct, tag(derive(key, 'mac'), dir, seq, aad, nonce, ct)))}`;
}

/** Opens a message: { seq, text } or null for anything that is not exactly a message of this key, direction and context. */
export function open(key, dir, aad, message) {
  if (typeof message !== 'string' || message.length > (4 << 20)) return null;
  const parts = message.split('.');
  if (parts.length !== 3 || parts[0] !== '1' || !/^(0|[1-9][0-9]{0,14})$/.test(parts[1])) return null;
  const seq = Number(parts[1]);
  const body = fromB64url(parts[2]);
  if (!body || body.length < 48) return null;
  const nonce = body.subarray(0, 16);
  const ct = body.subarray(16, body.length - 32);
  if (!equal(tag(derive(key, 'mac'), dir, seq, aad, nonce, ct), body.subarray(body.length - 32))) return null;
  try {
    return { seq, text: new TextDecoder('utf-8', { fatal: true }).decode(keystream(derive(key, 'enc'), nonce, ct)) };
  } catch {
    return null;
  }
}
