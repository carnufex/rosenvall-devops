import test from 'node:test';
import assert from 'node:assert/strict';
import { createApiClient } from './apiClient.ts';

test('retries a 401 response once with a refreshed access token', async () => {
  let token = 'stale-token';
  let refreshCount = 0;
  const authorizations: Array<string | null> = [];
  const fetchMock = async (_path: string, init?: RequestInit): Promise<Response> => {
    authorizations.push(new Headers(init?.headers).get('Authorization'));
    if (authorizations.length === 1) {
      return Response.json({ detail: 'expired' }, { status: 401, statusText: 'Unauthorized' });
    }

    return Response.json({ ok: true });
  };
  const client = createApiClient({
    fetch: fetchMock,
    getAccessToken: () => token,
    refreshAccessToken: async () => {
      refreshCount += 1;
      token = 'fresh-token';
      return token;
    }
  });

  const result = await client.get<{ ok: boolean }>('/api/me');

  assert.deepEqual(result, { ok: true });
  assert.equal(refreshCount, 1);
  assert.deepEqual(authorizations, ['Bearer stale-token', 'Bearer fresh-token']);
});

test('awaits the access token provider before sending a request', async () => {
  const authorizations: Array<string | null> = [];
  const client = createApiClient({
    fetch: async (_path, init) => {
      authorizations.push(new Headers(init?.headers).get('Authorization'));
      return Response.json({ ok: true });
    },
    getAccessToken: async () => 'stored-fresh-token'
  });

  await client.get('/api/workspaces');

  assert.deepEqual(authorizations, ['Bearer stored-fresh-token']);
});

test('does not retry a 401 response when token refresh is unavailable', async () => {
  const client = createApiClient({
    fetch: async () => Response.json({ detail: 'expired' }, { status: 401, statusText: 'Unauthorized' }),
    getAccessToken: () => 'stale-token'
  });

  await assert.rejects(() => client.get('/api/me'), /expired/);
});

test('runs the unauthorized handler when a refreshed token is still rejected', async () => {
  let unauthorizedCount = 0;
  const client = createApiClient({
    fetch: async () => Response.json({ detail: 'expired' }, { status: 401, statusText: 'Unauthorized' }),
    getAccessToken: () => 'stale-token',
    refreshAccessToken: async () => 'fresh-token',
    handleUnauthorized: async () => {
      unauthorizedCount += 1;
    }
  });

  await assert.rejects(() => client.get('/api/me'), /expired/);

  assert.equal(unauthorizedCount, 1);
});
