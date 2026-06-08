import test from 'node:test';
import assert from 'node:assert/strict';
import { buildCodeLineRenderModel, codeHighlightLimitMessage, highlightCodeLines, languageForPath, languagesForHighlightPath, plainHighlightedLines, shouldHighlightCode, splitCodeLines, splitDiffLineForHighlight } from './codeHighlight.ts';

function tokenText(line: Array<{ content: string }>): string {
  return line.map((token) => token.content).join('');
}

test('languageForPath maps common source paths to shiki languages', () => {
  assert.equal(languageForPath('src/App.tsx'), 'tsx');
  assert.equal(languageForPath('src/main.ts'), 'typescript');
  assert.equal(languageForPath('package.json'), 'json');
  assert.equal(languageForPath('src/index.css'), 'css');
  assert.equal(languageForPath('index.html'), 'html');
  assert.equal(languageForPath('README.md'), 'markdown');
  assert.equal(languageForPath('deployment.yaml'), 'yaml');
  assert.equal(languageForPath('Dockerfile'), 'dockerfile');
  assert.equal(languageForPath('scripts/start-local-demo.ps1'), 'powershell');
  assert.equal(languageForPath('Program.cs'), 'csharp');
  assert.equal(languageForPath('unknown.custom'), 'plaintext');
});

test('languagesForHighlightPath loads only the language dependencies needed for a file', () => {
  assert.deepEqual(languagesForHighlightPath('src/App.tsx'), ['typescript', 'tsx']);
  assert.deepEqual(languagesForHighlightPath('src/main.jsx'), ['javascript', 'jsx']);
  assert.deepEqual(languagesForHighlightPath('package.json'), ['json']);
  assert.deepEqual(languagesForHighlightPath('unknown.custom'), []);
});

test('splitCodeLines preserves the source line count including trailing blank lines', () => {
  assert.deepEqual(splitCodeLines('const x = 1;\n\n'), ['const x = 1;', '', '']);
});

test('plainHighlightedLines preserves whitespace and line count', () => {
  const lines = plainHighlightedLines('  const x = 1;\n\treturn x;\n');

  assert.equal(lines.length, 3);
  assert.equal(tokenText(lines[0]), '  const x = 1;');
  assert.equal(tokenText(lines[1]), '\treturn x;');
  assert.equal(tokenText(lines[2]), ' ');
});

test('highlightCodeLines keeps plaintext fallback stable for unknown files', async () => {
  const lines = await highlightCodeLines('  alpha\n\tbeta\n', 'unknown.custom');

  assert.equal(lines.length, 3);
  assert.equal(tokenText(lines[0]), '  alpha');
  assert.equal(tokenText(lines[1]), '\tbeta');
  assert.equal(tokenText(lines[2]), ' ');
});

test('highlightCodeLines preserves content and line count for known source files', async () => {
  const lines = await highlightCodeLines('const value = { ok: true };\n', 'src/example.ts');

  assert.equal(lines.length, 2);
  assert.equal(tokenText(lines[0]), 'const value = { ok: true };');
  assert.equal(tokenText(lines[1]), ' ');
});

test('large code inputs skip syntax highlighting and explain the budget', async () => {
  const largeContent = `${Array.from({ length: 5001 }, (_, index) => `const value${index} = ${index};`).join('\n')}\n`;

  assert.equal(shouldHighlightCode(largeContent), false);
  assert.equal(codeHighlightLimitMessage(largeContent), 'Syntax highlighting disabled for 5002 loaded lines to keep the review view responsive.');

  const lines = await highlightCodeLines(largeContent, 'src/large.ts');

  assert.equal(lines.length, 5002);
  assert.equal(tokenText(lines[0]), 'const value0 = 0;');
  assert.equal(lines[0].some((token) => Boolean(token.color)), false);
});

test('splitDiffLineForHighlight isolates code content without changing diff anchors', () => {
  assert.deepEqual(splitDiffLineForHighlight('+const x = 1;', 'add'), {
    prefix: '+',
    code: 'const x = 1;',
    highlightable: true
  });
  assert.deepEqual(splitDiffLineForHighlight('-const x = 1;', 'delete'), {
    prefix: '-',
    code: 'const x = 1;',
    highlightable: true
  });
  assert.deepEqual(splitDiffLineForHighlight(' const x = 1;', 'context'), {
    prefix: ' ',
    code: 'const x = 1;',
    highlightable: true
  });
  assert.deepEqual(splitDiffLineForHighlight('@@ -1 +1 @@', 'hunk'), {
    prefix: '',
    code: '@@ -1 +1 @@',
    highlightable: false
  });
});

test('buildCodeLineRenderModel normalizes source and diff lines through one contract', () => {
  assert.deepEqual(buildCodeLineRenderModel({
    kind: 'source',
    path: 'src/App.tsx',
    rawLineText: '  const value = 1;',
    lineNumber: 12
  }), {
    kind: 'source',
    path: 'src/App.tsx',
    rawLineText: '  const value = 1;',
    codeText: '  const value = 1;',
    prefix: '',
    highlightable: true,
    lineNumber: 12,
    oldLineNumber: null,
    newLineNumber: null
  });

  assert.deepEqual(buildCodeLineRenderModel({
    kind: 'diff-add',
    path: 'src/App.tsx',
    rawLineText: '+  const value = 1;',
    oldLineNumber: null,
    newLineNumber: 7
  }), {
    kind: 'diff-add',
    path: 'src/App.tsx',
    rawLineText: '+  const value = 1;',
    codeText: '  const value = 1;',
    prefix: '+',
    highlightable: true,
    lineNumber: null,
    oldLineNumber: null,
    newLineNumber: 7
  });

  assert.deepEqual(buildCodeLineRenderModel({
    kind: 'diff-meta',
    path: 'src/App.tsx',
    rawLineText: '@@ -1 +1 @@'
  }), {
    kind: 'diff-meta',
    path: 'src/App.tsx',
    rawLineText: '@@ -1 +1 @@',
    codeText: '@@ -1 +1 @@',
    prefix: '',
    highlightable: false,
    lineNumber: null,
    oldLineNumber: null,
    newLineNumber: null
  });
});
