import test from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, createApiClient, isApiError } from './apiClient.ts';

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

test('preserves structured problem details on failed responses', async () => {
  const client = createApiClient({
    fetch: async () => Response.json({
      type: 'https://example.test/problems/validation',
      title: 'Validation failed',
      detail: 'The request has validation errors.',
      errors: {
        ref: ['Invalid ref.'],
        name: 'Name is required.'
      }
    }, { status: 422, statusText: 'Unprocessable Entity', headers: { 'Retry-After': '5' } }),
    getAccessToken: () => null
  });

  await assert.rejects(async () => {
    await client.get('/api/repositories/1/source/tree');
  }, (error) => {
    assert.equal(error instanceof ApiError, true);
    assert.equal(isApiError(error), true);
    const apiError = error as ApiError;
    assert.equal(apiError.message, 'The request has validation errors.');
    assert.equal(apiError.status, 422);
    assert.equal(apiError.statusText, 'Unprocessable Entity');
    assert.equal(apiError.title, 'Validation failed');
    assert.equal(apiError.detail, 'The request has validation errors.');
    assert.equal(apiError.type, 'https://example.test/problems/validation');
    assert.deepEqual(apiError.errors, { ref: ['Invalid ref.'], name: ['Name is required.'] });
    assert.equal(apiError.requestPath, '/api/repositories/1/source/tree');
    assert.equal(apiError.retryAfter, '5');
    assert.match(apiError.rawBody ?? '', /Validation failed/);
    return true;
  });
});

test('preserves non-json error bodies as raw response text', async () => {
  const client = createApiClient({
    fetch: async () => new Response('plain upstream failure', { status: 502, statusText: 'Bad Gateway' }),
    getAccessToken: () => null
  });

  await assert.rejects(async () => {
    await client.get('/api/source');
  }, (error) => {
    assert.equal(isApiError(error), true);
    const apiError = error as ApiError;
    assert.equal(apiError.message, '502 Bad Gateway');
    assert.equal(apiError.status, 502);
    assert.equal(apiError.rawBody, 'plain upstream failure');
    assert.equal(apiError.title, null);
    assert.equal(apiError.errors, null);
    return true;
  });
});

test('returns undefined for 204 responses', async () => {
  const client = createApiClient({
    fetch: async () => new Response(null, { status: 204 }),
    getAccessToken: () => null
  });

  assert.equal(await client.delete('/api/work-items/1/comments/2'), undefined);
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

test('aborts a request when a per-request timeout expires', async () => {
  const client = createApiClient({
    fetch: async (_path, init) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    }),
    getAccessToken: () => null
  });

  await assert.rejects(() => client.get('/api/integrations/github/repository-picker', { timeoutMs: 10 }), /timed out/i);
});

test('aborts a request when the caller signal is cancelled', async () => {
  const controller = new AbortController();
  const client = createApiClient({
    fetch: async (_path, init) => new Promise<Response>((_resolve, reject) => {
      if (init?.signal?.aborted) {
        reject(new DOMException('Aborted', 'AbortError'));
        return;
      }

      init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    }),
    getAccessToken: () => null
  });

  const request = client.get('/api/repositories/1/source/tree', { signal: controller.signal, timeoutMs: 0 });
  controller.abort();

  await assert.rejects(request, /cancelled/i);
});

test('aborts a request when the default timeout expires', async () => {
  const originalSetTimeout = globalThis.setTimeout;
  const originalClearTimeout = globalThis.clearTimeout;
  try {
    globalThis.setTimeout = ((handler: TimerHandler) => {
      if (typeof handler === 'function') handler();
      return 1 as unknown as ReturnType<typeof setTimeout>;
    }) as typeof setTimeout;
    globalThis.clearTimeout = (() => undefined) as typeof clearTimeout;

    const client = createApiClient({
      fetch: async (_path, init) => new Promise<Response>((_resolve, reject) => {
        if (init?.signal?.aborted) {
          reject(new DOMException('Aborted', 'AbortError'));
          return;
        }
        init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      }),
      getAccessToken: () => null
    });

    await assert.rejects(async () => {
      await client.get('/api/workspaces');
    }, (error) => {
      assert.equal(isApiError(error), true);
      assert.equal((error as ApiError).status, 0);
      assert.match((error as Error).message, /timed out after 30000 ms/i);
      return true;
    });
  } finally {
    globalThis.setTimeout = originalSetTimeout;
    globalThis.clearTimeout = originalClearTimeout;
  }
});
