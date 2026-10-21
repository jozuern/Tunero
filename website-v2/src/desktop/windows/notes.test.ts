import { test } from 'node:test';
import assert from 'node:assert/strict';
import { blocks } from './notes.ts';

test('changelog markdown becomes headings, paragraphs and list items with bold and code spans', () => {
  const parsed = blocks('## 1.4.0\n\nIntro line.\n\n### Added\n\n- **Media:** an ISO and `choco` too.\n- Plain item.');
  assert.deepEqual(parsed, [
    { kind: 'h2', spans: [{ text: '1.4.0' }] },
    { kind: 'p', spans: [{ text: 'Intro line.' }] },
    { kind: 'h3', spans: [{ text: 'Added' }] },
    { kind: 'li', spans: [{ text: 'Media:', bold: true }, { text: ' an ISO and ' }, { text: 'choco', code: true }, { text: ' too.' }] },
    { kind: 'li', spans: [{ text: 'Plain item.' }] },
  ]);
});
