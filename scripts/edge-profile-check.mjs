// Checks the temporary Edge profile cleanup on a fake temp folder (no Edge is started).
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, utimesSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { sweepStale, PREFIX } from './edge-profile.mjs';

const root = mkdtempSync(join(tmpdir(), 'edge-profile-check-'));
const old = new Date(Date.now() - 3 * 3600 * 1000);
const make = (name, aged = true) => {
  const d = join(root, name);
  mkdirSync(d);
  writeFileSync(join(d, 'f.txt'), 'x');
  if (aged) utimesSync(d, old, old);
  return d;
};
try {
  const stale = make(`${PREFIX}111-abc`);
  const staleLegacy = make(`${PREFIX}xyz`);
  const fresh = make(`${PREFIX}222-abc`, false);
  const running = make(`${PREFIX}333-abc`);
  const foreign = make('someone-elses-folder');
  const foreignPrefix = make('megahub-brand-111-abc');
  const target = make('link-target', false); // a folder outside our prefix, reachable only through a link
  const link = join(root, `${PREFIX}444-link`);
  let linked = true;
  try { symlinkSync(target, link, process.platform === 'win32' ? 'junction' : 'dir'); } catch { linked = false; }
  const file = join(root, `${PREFIX}555-file`);
  writeFileSync(file, 'x'); utimesSync(file, old, old);

  // now is far ahead, so even the fresh-looking link counts as old: only the link check can keep it.
  const removed = sweepStale({ root, now: Date.now() + 10 * 3600 * 1000, isAlive: (pid) => pid === 333 });
  assert.ok(!existsSync(stale), 'stale folder is removed');
  assert.ok(!existsSync(staleLegacy), 'stale folder without a pid is removed');
  assert.ok(!existsSync(fresh), 'with the clock moved on, the former fresh folder is old too');
  assert.ok(existsSync(running), 'old folder of a running owner is kept');
  assert.ok(existsSync(foreign) && existsSync(foreignPrefix), 'folders with another prefix are not touched');
  assert.ok(existsSync(file), 'a plain file with our prefix is not touched');
  if (linked) {
    assert.ok(existsSync(link), 'the link itself is skipped');
    assert.ok(existsSync(join(target, 'f.txt')), 'the link is not followed');
  } else console.log('edge-profile: link part skipped (no permission to create links)');
  assert.deepEqual(removed.sort(), [`${PREFIX}111-abc`, `${PREFIX}222-abc`, `${PREFIX}xyz`].sort());

  // Fresh folder is kept with the real clock.
  const fresh2 = make(`${PREFIX}666-abc`, false);
  assert.deepEqual(sweepStale({ root, isAlive: () => false }), [`${PREFIX}333-abc`]);
  assert.ok(existsSync(fresh2), 'fresh folder is kept');
  console.log('edge-profile: OK');
} finally {
  rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
}
