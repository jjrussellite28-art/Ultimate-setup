// Shared by every script that starts a headless Edge with a temporary profile (--user-data-dir): the profile folder is removed
// on every way out (normal end, error, SIGINT/SIGTERM, uncaught exception), and folders an earlier run left behind (killed
// hard, so nothing could clean up) are swept on the next start. Only folders named `couchhud-edge-<pid>-<random>` directly in
// the temp folder are ever touched, and never through a link.
import { spawnSync } from 'node:child_process';
import { lstatSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const PREFIX = 'couchhud-edge-';
const STALE_MS = 60 * 60 * 1000;

const alive = (pid) => { try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; } };

// Delete with retries and backoff (Edge children may hold files for a moment after they are gone). rmSync does not follow links.
export function removeDir(dir) {
  try { rmSync(dir, { recursive: true, force: true, maxRetries: 12, retryDelay: 250 }); return true; } catch { return false; }
}

// Remove old profile folders: own prefix, plain directory (lstat: a link or junction is skipped), older than maxAgeMs,
// and the process named in the folder name is gone. Returns the names removed.
export function sweepStale({ root = tmpdir(), maxAgeMs = STALE_MS, now = Date.now(), isAlive = alive } = {}) {
  const removed = [];
  let names;
  try { names = readdirSync(root); } catch { return removed; }
  for (const name of names) {
    if (!name.startsWith(PREFIX)) continue;
    const full = join(root, name);
    try {
      const st = lstatSync(full);
      if (st.isSymbolicLink() || !st.isDirectory()) continue;
      if (now - st.mtimeMs < maxAgeMs) continue;
      const pid = /^(\d+)-/.exec(name.slice(PREFIX.length));
      if (pid && isAlive(Number(pid[1]))) continue;
      if (removeDir(full)) removed.push(name);
    } catch { /* gone already, or not ours to read */ }
  }
  return removed;
}

// Edge is a process tree; on Windows only taskkill /T ends the children that hold the profile folder
// (only while Edge runs: a finished PID may be reused).
export function killTree(child) {
  if (!child) return;
  if (process.platform === 'win32' && child.pid && child.exitCode === null && child.signalCode === null) spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
  child.kill();
}

// Sweep old folders, then make this run's profile folder.
export function createProfile() {
  sweepStale();
  return mkdtempSync(join(tmpdir(), `${PREFIX}${process.pid}-`));
}

// Make sure the processes and the folders are cleaned up however this process ends. `children` are killed, the `browser` tree is
// killed, `dirs` removed. Returns async release(): wait for the browser to really exit (up to 5 s), then remove the folders.
export function guard({ children = [], browser, dirs = [] }) {
  let done = false;
  const cleanup = () => {
    for (const c of children) try { c.kill(); } catch { /* gone */ }
    killTree(browser);
    if (done) return;
    done = true;
    // Synchronous retries: this also runs in an 'exit' handler, which cannot wait.
    for (const d of dirs) removeDir(d);
  };
  process.on('exit', cleanup);
  for (const sig of ['SIGINT', 'SIGTERM']) process.on(sig, () => process.exit(130));
  process.on('uncaughtException', (e) => { console.error(e); process.exit(1); });
  const gone = browser ? new Promise((r) => browser.once('exit', r)) : Promise.resolve();
  return async function release() {
    for (const c of children) try { c.kill(); } catch { /* gone */ }
    killTree(browser);
    await Promise.race([gone, new Promise((r) => setTimeout(r, 5000))]);
    cleanup();
  };
}
