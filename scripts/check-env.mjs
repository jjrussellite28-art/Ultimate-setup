// Shared by the checks that start a headless Edge: a free port from the system (two runs at the same time never share a port) and
// the Edge to start (EDGE, else the usual install places; only a file that exists).
import { existsSync } from 'node:fs';
import net from 'node:net';

export const freePort = () => new Promise((res, rej) => {
  const s = net.createServer();
  s.once('error', rej);
  s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => res(String(port))); });
});

export function findEdge() {
  const candidates = process.env.EDGE ? [process.env.EDGE] : [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    '/usr/bin/microsoft-edge',
    '/usr/bin/microsoft-edge-stable',
    '/opt/microsoft/msedge/msedge',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge'
  ];
  return candidates.find((p) => existsSync(p));
}
