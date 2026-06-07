import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { normalizeFrontendReleaseDiagnostics } from './releaseDiagnostics.ts';

test('frontend release diagnostics normalize bundle metadata from vite env', () => {
  assert.deepEqual(normalizeFrontendReleaseDiagnostics({
    VITE_RELEASE_COMMIT_SHA: '  abcdef1234567890  ',
    VITE_RELEASE_BUILD_TIMESTAMP: '  2026-06-07T10:20:30Z  '
  }), {
    commitSha: 'abcdef1234567890',
    buildTimestamp: '2026-06-07T10:20:30Z'
  });

  assert.deepEqual(normalizeFrontendReleaseDiagnostics({}), {
    commitSha: 'unknown',
    buildTimestamp: 'unknown'
  });
});

test('frontend image build embeds release metadata into the vite bundle', () => {
  const dockerfile = readFileSync(new URL('../Dockerfile', import.meta.url), 'utf8');
  const workflow = readFileSync(new URL('../../.github/workflows/publish-images.yml', import.meta.url), 'utf8');
  const viteConfig = readFileSync(new URL('../vite.config.ts', import.meta.url), 'utf8');
  const appSource = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8');

  assert.match(dockerfile, /ARG VITE_RELEASE_COMMIT_SHA=/);
  assert.match(dockerfile, /ARG VITE_RELEASE_BUILD_TIMESTAMP=/);
  assert.match(workflow, /VITE_RELEASE_COMMIT_SHA=\$\{\{ env\.COMMIT_SHA \}\}/);
  assert.match(workflow, /VITE_RELEASE_BUILD_TIMESTAMP=\$\{\{ env\.BUILD_TIMESTAMP \}\}/);
  assert.match(viteConfig, /VITE_RELEASE_COMMIT_SHA/);
  assert.match(viteConfig, /VITE_RELEASE_BUILD_TIMESTAMP/);
  assert.match(appSource, /frontendReleaseDiagnostics/);
  assert.match(appSource, /Frontend bundle/);
});
