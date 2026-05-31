export type AuthSession = {
  getAccessToken: () => string | null | Promise<string | null>;
  refreshAccessToken?: () => Promise<string | null>;
  handleUnauthorized?: () => Promise<void>;
};

type ApiClientOptions = AuthSession & {
  fetch?: (path: string, init?: RequestInit) => Promise<Response>;
};

type RequestOptions = {
  timeoutMs?: number;
  signal?: AbortSignal;
};

const DEFAULT_TIMEOUT_MS = 30000;

export type ApiProblemErrors = Record<string, string[]>;

export type ApiErrorOptions = {
  status: number;
  statusText?: string;
  title?: string | null;
  detail?: string | null;
  type?: string | null;
  errors?: ApiProblemErrors | null;
  rawBody?: string | null;
  requestPath: string;
  retryAfter?: string | null;
};

export class ApiError extends Error {
  readonly status: number;
  readonly statusText: string;
  readonly title: string | null;
  readonly detail: string | null;
  readonly type: string | null;
  readonly errors: ApiProblemErrors | null;
  readonly rawBody: string | null;
  readonly requestPath: string;
  readonly retryAfter: string | null;

  constructor(options: ApiErrorOptions) {
    const fallback = options.status > 0
      ? `${options.status} ${options.statusText || 'Request failed'}`
      : options.title || 'Request failed';
    super(options.detail || options.title || fallback);
    this.name = 'ApiError';
    this.status = options.status;
    this.statusText = options.statusText || '';
    this.title = options.title ?? null;
    this.detail = options.detail ?? null;
    this.type = options.type ?? null;
    this.errors = options.errors ?? null;
    this.rawBody = options.rawBody ?? null;
    this.requestPath = options.requestPath;
    this.retryAfter = options.retryAfter ?? null;
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError ||
    (typeof error === 'object' &&
      error !== null &&
      (error as { name?: unknown }).name === 'ApiError' &&
      typeof (error as { status?: unknown }).status === 'number' &&
      typeof (error as { requestPath?: unknown }).requestPath === 'string');
}

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

  const request = async <T>(path: string, init?: RequestInit, requestOptions?: RequestOptions): Promise<T> => {
    const timeoutMs = requestOptions?.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    const controller = timeoutMs > 0 || requestOptions?.signal ? new AbortController() : null;
    const abortFromCaller = () => controller?.abort();
    if (controller && requestOptions?.signal) {
      if (requestOptions.signal.aborted) {
        controller.abort();
      } else {
        requestOptions.signal.addEventListener('abort', abortFromCaller, { once: true });
      }
    }
    const timeout = controller && timeoutMs > 0 ? globalThis.setTimeout(() => controller.abort(), timeoutMs) : null;
    const requestInit = controller ? { ...init, signal: controller.signal } : init;
    try {
      let response = await fetchImpl(path, await withAuth(requestInit, options.getAccessToken));
      if (response.status === 401) {
        const refreshedToken = await refreshOnce();
        if (refreshedToken) {
          response = await fetchImpl(path, await withAuth(requestInit, () => refreshedToken));
        }
        if (response.status === 401) {
          await options.handleUnauthorized?.();
        }
      }

      return parseResponse<T>(response, path);
    } catch (error) {
      if (controller?.signal.aborted) {
        if (requestOptions?.signal?.aborted) {
          throw new ApiError({
            status: 0,
            title: 'Request cancelled',
            detail: `Request to ${path} was cancelled`,
            requestPath: path
          });
        }

        throw new ApiError({
          status: 0,
          title: 'Request timed out',
          detail: `Request to ${path} timed out after ${timeoutMs} ms`,
          requestPath: path
        });
      }
      throw error;
    } finally {
      if (timeout) globalThis.clearTimeout(timeout);
      requestOptions?.signal?.removeEventListener('abort', abortFromCaller);
    }
  };

  return {
    get<T>(path: string, options?: RequestOptions): Promise<T> {
      return request<T>(path, undefined, options);
    },
    post<T>(path: string, body: unknown, options?: RequestOptions): Promise<T> {
      return request<T>(path, jsonRequest('POST', body), options);
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

async function parseResponse<T>(response: Response, path: string): Promise<T> {
  if (!response.ok) {
    throw await apiErrorFromResponse(response, path);
  }
  if (response.status === 204) return undefined as T;
  return response.json();
}

async function apiErrorFromResponse(response: Response, path: string): Promise<ApiError> {
  const rawBody = await response.text();
  const payload = parseProblemDetails(rawBody);
  return new ApiError({
    status: response.status,
    statusText: response.statusText,
    title: typeof payload?.title === 'string' ? payload.title : null,
    detail: typeof payload?.detail === 'string' ? payload.detail : null,
    type: typeof payload?.type === 'string' ? payload.type : null,
    errors: normalizeProblemErrors(payload?.errors),
    rawBody,
    requestPath: path,
    retryAfter: response.headers.get('Retry-After')
  });
}

function parseProblemDetails(rawBody: string): Record<string, unknown> | null {
  if (!rawBody.trim()) return null;
  try {
    const parsed = JSON.parse(rawBody);
    return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
      ? parsed as Record<string, unknown>
      : null;
  } catch {
    return null;
  }
}

function normalizeProblemErrors(value: unknown): ApiProblemErrors | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null;
  const result: ApiProblemErrors = {};
  for (const [key, entry] of Object.entries(value)) {
    if (Array.isArray(entry)) {
      const messages = entry.filter((message): message is string => typeof message === 'string');
      if (messages.length > 0) result[key] = messages;
    } else if (typeof entry === 'string') {
      result[key] = [entry];
    }
  }

  return Object.keys(result).length > 0 ? result : null;
}
