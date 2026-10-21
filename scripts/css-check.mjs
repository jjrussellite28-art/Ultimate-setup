// CSS syntax check without packages: node scripts/css-check.mjs [dir ...]  (default: ui)
// A tokenizer that skips comments and strings and checks that braces, brackets and parentheses balance, and that a
// rule block never opens inside a declaration block's declaration (a missing "}" swallows every rule after it:
// the nested rules then silently do nothing). Plain CSS nesting is not used in this project, so a style rule inside a
// style rule is reported; @media / @supports / @layer / @keyframes / @font-face may contain rules as usual.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

const roots = process.argv.slice(2).length ? process.argv.slice(2) : ['ui'];
const files = [];
const walk = (p) => {
  if (statSync(p).isDirectory()) for (const f of readdirSync(p)) walk(join(p, f));
  else if (p.endsWith('.css')) files.push(p);
};
roots.forEach(walk);

// At-rules whose block contains rules (everything else, like @font-face, holds declarations).
const GROUPING = /^@(media|supports|layer|container|document|-moz-document|scope)\b/i;

function check(file) {
  const src = readFileSync(file, 'utf8');
  const problems = [];
  const line = (i) => src.slice(0, i).split('\n').length;
  // stack entries: { kind: 'group' | 'style' | 'decl', at } and pairs of ( [
  const blocks = [];
  const pairs = [];
  let preludeStart = 0;
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    if (c === '/' && src[i + 1] === '*') {
      const end = src.indexOf('*/', i + 2);
      if (end < 0) {
        problems.push(`${file}:${line(i)}: unterminated comment`);
        return problems;
      }
      i = end + 2;
      continue;
    }
    if (c === '"' || c === "'") {
      let j = i + 1;
      while (j < src.length && src[j] !== c && src[j] !== '\n') j += src[j] === '\\' ? 2 : 1;
      if (src[j] !== c) problems.push(`${file}:${line(i)}: unterminated string`);
      i = j + 1;
      continue;
    }
    if (c === '(' || c === '[') pairs.push({ c, at: i });
    else if (c === ')' || c === ']') {
      const open = pairs.pop();
      if (!open || (open.c === '(') !== (c === ')')) problems.push(`${file}:${line(i)}: unbalanced "${c}"`);
    } else if (c === '{') {
      const prelude = src.slice(preludeStart, i).replace(/\/\*[\s\S]*?\*\//g, '').trim();
      const parent = blocks.at(-1);
      let kind;
      if (prelude.startsWith('@')) kind = GROUPING.test(prelude) || /^@keyframes|^@-webkit-keyframes/i.test(prelude) ? 'group' : 'decl';
      else kind = 'style';
      // A selector block directly inside a style block is the signature of a missing "}".
      if (parent?.kind === 'style' && kind === 'style') problems.push(`${file}:${line(i)}: rule "${prelude.slice(0, 60)}" starts inside the unclosed rule from line ${line(parent.at)} (missing "}"?)`);
      blocks.push({ kind, at: i });
      preludeStart = i + 1;
    } else if (c === '}') {
      if (!blocks.length) problems.push(`${file}:${line(i)}: unexpected "}"`);
      else blocks.pop();
      preludeStart = i + 1;
    } else if (c === ';' && !pairs.length) {
      preludeStart = i + 1;
    }
    i++;
  }
  for (const b of blocks) problems.push(`${file}:${line(b.at)}: "{" is never closed`);
  for (const p of pairs) problems.push(`${file}:${line(p.at)}: "${p.c}" is never closed`);
  return problems;
}

const all = files.flatMap(check);
if (all.length) {
  console.error(all.join('\n'));
  console.error(`css: ${all.length} problem(s)`);
  process.exit(1);
}
console.log(`css: ok (${files.length} files)`);
