// Pure UI rules with a test: node scripts/ui-logic-check.mjs (run by check.sh).
import assert from 'node:assert/strict';
import { clampPaste, remoteView } from '../ui/core/paste.js';

// A pasted text is cut to the field's limit.
assert.equal(clampPaste('abcdef', 4), 'abcd');
assert.equal(clampPaste('abcdef', 0), 'abcdef');
assert.equal(clampPaste(null, 4), '');

// The remote sees nothing of a secret field, nor of a pasted one until it is edited by hand.
assert.deepEqual(remoteView({ secret: true, text: 'token' }), { secret: true, text: '' });
assert.deepEqual(remoteView({ secret: false, pasted: true, text: 'token' }), { secret: true, text: '' });
assert.deepEqual(remoteView({ secret: false, pasted: false, text: 'https://x' }), { secret: false, text: 'https://x' });
assert.deepEqual(remoteView(null), { secret: false, text: '' });

console.log('ui logic: ok');
