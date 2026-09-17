/**
 * Thrown for any non-2xx Gateway response. Carries the status so callers can
 * branch on it (a 404 usually means "show notFound()" or "no viewer yet",
 * anything else usually means "show an error state") without re-parsing the
 * response themselves.
 */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly path: string,
  ) {
    super(`Gateway request to "${path}" failed with status ${status}`);
    this.name = "ApiError";
  }
}

/**
 * The core every Gateway client (server-side and browser-side) shares — see
 * `lib/api/client.ts` and `lib/api/browser-client.ts`. Takes the base URL as
 * a parameter rather than importing one directly, because the two contexts
 * deliberately read it from different environment variables (one is
 * server-only, one must be `NEXT_PUBLIC_*` to reach the browser bundle).
 */
export async function requestJson<T>(
  baseUrl: string,
  path: string,
  init?: RequestInit,
): Promise<T> {
  const response = await fetch(`${baseUrl}${path}`, {
    ...init,
    headers: {
      Accept: "application/json",
      ...init?.headers,
    },
  });

  if (!response.ok) {
    throw new ApiError(response.status, path);
  }

  return (await response.json()) as T;
}

/**
 * Resolves a 404 to `null` instead of throwing. Use for "this resource might
 * legitimately not exist" reads (a video, a stored viewer) — let a genuine
 * 404 propagate everywhere else.
 */
export async function requestJsonOrNull<T>(
  baseUrl: string,
  path: string,
  init?: RequestInit,
): Promise<T | null> {
  try {
    return await requestJson<T>(baseUrl, path, init);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      return null;
    }
    throw error;
  }
}

/**
 * For mutations whose success response carries no body — every reaction
 * endpoint returns 204. Deliberately separate from {@link requestJson}
 * rather than having that function guess based on status code: a caller
 * expecting real JSON back should still get a clear parse failure if a body
 * it needed turns out to be empty, not have that silently coerced away.
 */
export async function requestVoid(
  baseUrl: string,
  path: string,
  init?: RequestInit,
): Promise<void> {
  const response = await fetch(`${baseUrl}${path}`, init);

  if (!response.ok) {
    throw new ApiError(response.status, path);
  }
}
