export type AuthSession = {
  getAccessToken: () => string | null | Promise<string | null>;
  refreshAccessToken?: () => Promise<string | null>;
  handleUnauthorized?: () => Promise<void>;
};

type ApiClientOptions = AuthSession & {
  fetch?: (path: string, init?: RequestInit) => Promise<Response>;
};

export function createApiClient(options: ApiClientOptions) {
  const fetchImpl = options.fetch ?? ((path, init) => fetch(path, init));
  let refreshInFlight: Promise<string | null> | null = null;

  const refreshOnce = async () => {
    if (!options.refreshAccessToken) return null;
    refreshInFlight ??= options.refreshAccessToken().finally(() => {
      refreshInFlight = null;
    });
    return refreshInFlight;
  };

  const request = async <T>(path: string, init?: RequestInit): Promise<T> => {
    let response = await fetchImpl(path, await withAuth(init, options.getAccessToken));
    if (response.status === 401) {
      const refreshedToken = await refreshOnce();
      if (refreshedToken) {
        response = await fetchImpl(path, await withAuth(init, () => refreshedToken));
      }
      if (response.status === 401) {
        await options.handleUnauthorized?.();
      }
    }

    return parseResponse<T>(response);
  };

  return {
    get<T>(path: string): Promise<T> {
      return request<T>(path);
    },
    post<T>(path: string, body: unknown): Promise<T> {
      return request<T>(path, jsonRequest('POST', body));
    },
    patch<T>(path: string, body: unknown): Promise<T> {
      return request<T>(path, jsonRequest('PATCH', body));
    },
    put<T>(path: string, body: unknown): Promise<T> {
      return request<T>(path, jsonRequest('PUT', body));
    },
    async delete(path: string): Promise<void> {
      await request<void>(path, { method: 'DELETE' });
    }
  };
}

function jsonRequest(method: string, body: unknown): RequestInit {
  return {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  };
}

async function withAuth(init: RequestInit | undefined, getAccessToken: () => string | null | Promise<string | null>): Promise<RequestInit> {
  const headers = new Headers(init?.headers);
  const token = await getAccessToken();
  if (token) {
    headers.set('Authorization', `Bearer ${token}`);
  } else {
    headers.delete('Authorization');
  }

  return { ...init, headers };
}

async function parseResponse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const payload = await response.json() as { detail?: string; title?: string };
      message = payload.detail || payload.title || message;
    } catch {
      // Keep status text when the server did not return JSON problem details.
    }
    throw new Error(message);
  }
  if (response.status === 204) return undefined as T;
  return response.json();
}
