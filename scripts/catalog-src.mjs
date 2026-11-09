// Where the catalog source checkout (couchhud-catalog-src) lives, for the skin checks.
// 1. COUCHHUD_CATALOG_SRC (the checkout itself, or its skins folder), 2. the sibling of this checkout,
// 3. the sibling of the main checkout (a git worktree sits somewhere else, e.g. .claude/worktrees/x; git names the main repo).
import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('.', import.meta.url)), '..');

export function catalogSkinsDir() {
  const candidates = [];
  if (process.env.MEGAHUB_CATALOG_SKINS) candidates.push(process.env.MEGAHUB_CATALOG_SKINS);
  const env = process.env.COUCHHUD_CATALOG_SRC;
  if (env) candidates.push(existsSync(join(env, 'skins')) ? join(env, 'skins') : env);
  candidates.push(join(root, '..', 'couchhud-catalog-src', 'skins'));
  const git = spawnSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], { cwd: root, encoding: 'utf8' });
  if (git.status === 0 && git.stdout.trim()) candidates.push(join(dirname(git.stdout.trim()), '..', 'couchhud-catalog-src', 'skins'));
  return candidates.map((c) => resolve(c)).find((c) => existsSync(c)) ?? resolve(candidates[env ? 0 : 1]);
}
