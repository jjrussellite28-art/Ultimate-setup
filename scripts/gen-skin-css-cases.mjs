// Writes tests/skin-css-cases.json: the cases both implementations of the skin stylesheet sanitizer (scripts/skin-css.js and SkinCss in Core) must agree on.
// Hand written cases (the forms of @import and @layer, the ones the reviews found: url tokens that swallow "/*", strings that end at a carriage return or a
// form feed) and a fixed-seed fuzz corpus; the expected output is what scripts/skin-css.js gives, checked by eye once and then pinned: the C# tests and
// scripts/ui-logic-check.mjs compare against it, and ui-logic-check also feeds every output to headless Edge to see that no @layer or @import rule is left.
//
//   node scripts/gen-skin-css-cases.mjs --write
import { createRequire } from 'node:module';
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const require = createRequire(import.meta.url);
const { sanitize } = require('./skin-css.js');
const here = dirname(fileURLToPath(import.meta.url));
const B = '\\';

/** The hand written inputs: [name, css]. */
export const HAND = [
  ['import-url-external', '@import url(https://fonts.example/x.css);'],
  ['import-string-external', '@import "https://fonts.example/x.css";'],
  ['import-layer', '@import url(x.css) layer(ch-pins);a{b:c}'],
  ['import-layer-supports', '@import url("x.css") layer(a) supports(display:grid) screen;a{b:c}'],
  ['import-upper', "@IMPORT 'x.css';a{b:c}"],
  ['import-escape', '@' + B + '69 mport url(x.css);a{b:c}'],
  ['import-comment-in-prelude', '@import url(x.css) /* ; */ layer;a{b:c}'],
  ['layer-statement', '@layer ch-pins;a{b:c}'],
  ['layer-statement-list', '@layer a, b, ch-pins;a{b:c}'],
  ['layer-upper', '@LAYER ch-pins;a{b:c}'],
  ['layer-escape', '@' + B + '6c ayer ch-pins;a{b:c}'],
  ['layer-comment-name', '@layer/**/ch-pins;a{b:c}'],
  ['layer-block', '@layer ch-pins{[popover]::backdrop{background:#000!important}}'],
  ['layer-anonymous', '@layer{a{b:c}}'],
  ['layer-block-two-rules', '@layer x{a{b:c}d{e:f}}g{h:i}'],
  ['layer-nested', '@layer x{@layer y{a{b:c}}}d{e:f}'],
  ['layer-in-media', '@media (min-width:1px){@layer x{a{b:c}}d{e:f}}'],
  ['layer-after-media', '@media (min-width:1px){a{b:c}}@layer x{d{e:f}}'],
  ['plain-string-mentions', 'a{content:"@layer x;"}'],
  ['plain-string-import', "a{content:'@import url(x)'}"],
  ['plain-comment', '/* @layer x; @import url(x); */a{b:c}'],
  ['plain-url-word', 'a{background:url("x@layer.png")}'],
  ['plain-font-face', '@media screen{a{b:c}}@font-face{font-family:x;src:url(x.woff2)}'],
  ['plain-layerx', 'a{b:c}@layerx{d:e}'],
  ['unfinished-import', 'a{b:c}@import url(x'],
  ['unfinished-layer', 'a{b:c}@layer x'],
  ['unfinished-layer-block', 'a{b:c}@layer x{'],
  ['stray-close-brace', '@layer x{}}'],
  // The ones the reviews found: an unquoted url( runs to ")" and takes "/*" and quotes with it; strings end at CR and FF too.
  ['imp-unk-urlc', '@x url(/*);@import "e.css" layer(ch);/* */'],
  ['imp-unk-ff', '@x "\f;@import "e.css" layer(ch);"'],
  ['imp-unk-cr', '@x "\r;@import "e.css" layer(ch);"'],
  ['imp-charset-urlc', '@charset "utf-8";@x url(/*);@import "e.css" layer(ch);/* */'],
  ['imp-after-layerstmt-urlc', '@layer a;@import url(/*);@import "e.css" layer(ch);/* */'],
  ['imp-bare', '@import url(/*);@import "e.css" layer(ch);/* */'],
  ['layer-stmt-urlc', 'a{b:url(/*)}@layer ch,a;/* */'],
  ['layer-urlc', 'a{b:url(/*)}@layer ch{c{d:e}}/* */'],
  ['layer-urlc-ws', 'a{b:url( /*)}@layer ch{c{d:e}}/* */'],
  ['layer-in-supports', '@supports (b:url(/*)) {a{b:c}} @layer ch{c{d:e}} /* */'],
  ['layer-in-media-prelude', '@media (min-width:url(/*)){a{b:c}} @layer ch{c{d:e}} /* */'],
  ['layer-sel-attr', 'a[x=url(/*)]{b:c} @layer ch{c{d:e}} /* */'],
  ['layer-string-cr', 'a{content:"\r}@layer ch{x{y:z}}"}'],
  ['layer-string-ff', 'a{content:"\f}@layer ch{x{y:z}}"}'],
  ['layer-string-lf', 'a{content:"\n}@layer ch{x{y:z}}"}'],
  ['layer-string-crlf', 'a{content:"\r\n}@layer ch{x{y:z}}"}'],
  ['layer-url-quote', "a{b:url(')}@layer ch{c{d:e}}/* ')*/"],
  ['layer-url-escape-paren', 'a{b:url(x' + B + ')y/*)}@layer ch{c{d:e}}/* */'],
  ['layer-url-bad-then-comment', 'a{b:url(x y/*)}@layer ch{c{d:e}}/* */'],
  ['layer-upper-url', 'a{b:URL(/*)}@layer ch{c{d:e}}/* */'],
  ['layer-escaped-url', 'a{b:u' + B + '72l(/*)}@layer ch{c{d:e}}/* */'],
  ['layer-dimension-url', 'a{b:1url(/*)}@layer ch{c{d:e}}/* */'],
  ['layer-ident-ends-url', 'a{b:foo-url(/*)}@layer ch{c{d:e}}/* */'],
  ['layer-escaped-newline-string', 'a{content:"x' + B + '\n}@layer ch{c{d:e}}"}'],
  ['layer-after-bad-string-comment', 'a{content:"\n/*}@layer ch{c{d:e}}/* */'],
  ['url-quoted-ok', 'a{b:url("x.png")}c{d:url( \'y.png\' )}'],
  ['url-unquoted-ok', 'a{b:url(x.png)}c{d:url(  y.png  )}'],
  ['hidden-mention-in-string', 'a{content:"' + B + '40layer"}'],
  ['hidden-mention-in-comment', '/* @' + B + '6c ayer */a{b:c}'],
  ['hidden-mention-in-url', 'a{b:url(@layer)}'],
  // Another charset can hide text from a scan of the UTF-8 reading (iso-2022-jp: ESC ( B produces no character): refused, and @charset itself never survives.
  ['charset-utf8', '@charset "utf-8";a{b:c}'],
  ['charset-utf8-upper', '@charset "UTF-8" ;a{b:c}'],
  ['charset-utf8-single-quotes', "@charset 'utf-8';a{b:c}"],
  ['charset-not-first-utf8', 'a{b:c}@charset "utf-8";d{e:f}'],
  ['charset-iso2022jp-layer', '@charset "iso-2022-jp";\n@\u001b(Blayer ch{x{y:z}}'],
  ['charset-iso2022jp-layer-mid', '@charset "iso-2022-jp";\n@l\u001b(Bayer ch{x{y:z}}'],
  ['charset-iso2022jp-url', '@charset "iso-2022-jp";a{b:url(ht\u001b(Btps://x.example/y.png)}'],
  ['charset-iso2022jp-plain', '@charset "iso-2022-jp";a{b:c}'],
  ['charset-utf16', '@charset "utf-16";a{b:c}'],
  ['charset-utf16le', '@charset "UTF-16LE";\n@layer ch{x{y:z}}'],
  ['charset-shift-jis', '@charset "shift_jis";a{b:c}'],
  ['charset-windows-1252', '@charset "windows-1252";a{b:c}'],
  ['charset-utf8-prefix-trick', '@charset "utf-8x";a{b:c}'],
  ['charset-block', '@charset "iso-2022-jp"{a{b:c}}'],
  ['esc-alone', 'a{b:c}\u001b'],
  ['esc-before-layer', '\u001b(B@layer ch{x{y:z}}'],
  ['nul-in-layer', '@l\u0000ayer ch{x{y:z}}'],
  ['nul-in-url', 'a{b:url(ht\u0000tps://x.example/y.png)}'],
  ['so-si', 'a{b:c}\u000e\u000f'],
  ['vt', 'a{b:c}\u000b'],
  ['del-allowed', 'a{b:c}\u007f'],
  ['tab-lf-ff-cr-allowed', 'a{b:c}\t\n\f\r\na{d:e}']
];

/** A fixed-seed corpus of random token soups (the token list of the reviewer's fuzzer, plus url and string edges). */
export function fuzz(count, seedStart = 20241103) {
  let seed = seedStart;
  const rnd = () => (seed = (Math.imul(seed, 1103515245) + 12345) & 0x7fffffff) / 0x7fffffff;
  const T = ['@layer ch', '@layer ch{', '@layer a,b;', '@import "e.css"', '@import url(e.css)', '@import "e.css" layer(ch);', B + '40layer ch;', '@' + B + '6c ayer x;', 'url(', 'url(/*',
    '/*', '*/', '"', "'", B, '{', '}', ';', '(', ')', '[', ']', '\f', '\r', '\n', ' ', '\t', 'a', 'a{b:c}', '<!--', '-->', ':', 'x', '@media screen{', '@supports (a:b){', '@x ', 'u+',
    ' ', '\u0000', B + '\n', 'URL(', 'src:url(', "url('", 'url("', 'url( ', B + ')', '@font-face{', '@keyframes k{', '@layer', '1url(', '-url(', '@import', 'u' + B + '72l(', '1e', '.5', '+', '-'];
  const out = [];
  for (let i = 0; i < count; i++) {
    const len = 3 + Math.floor(rnd() * 12);
    let s = '';
    for (let j = 0; j < len; j++) s += T[Math.floor(rnd() * T.length)];
    out.push(s);
  }
  return out;
}

if (process.argv.includes('--write')) {
  const cases = HAND.map(([name, input]) => ({ name, input }));
  fuzz(400).forEach((input, i) => cases.push({ name: `fuzz-${i}`, input }));
  for (const c of cases) c.output = sanitize(c.input).css;
  writeFileSync(join(here, '..', 'tests', 'skin-css-cases.json'), JSON.stringify(cases, null, 1) + '\n');
  console.log(`${cases.length} cases, ${cases.filter((c) => c.output === '').length} refused`);
}
