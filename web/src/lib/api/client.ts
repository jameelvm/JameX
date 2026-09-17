import { gatewayBaseUrl } from "@/lib/config";
import { requestJson, requestJsonOrNull } from "@/lib/api/errors";

export { ApiError } from "@/lib/api/errors";

/**
 * Server-side Gateway calls — Server Components and anything else running on
 * the server. Do not import this from a Client Component; it reads a
 * server-only environment variable and will silently fall back to the
 * default base URL there. Use `lib/api/browser-client.ts` instead.
 */
export function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  return requestJson<T>(gatewayBaseUrl, path, init);
}

/** Same as {@link apiFetch}, but a 404 resolves to `null` instead of throwing. */
export function apiFetchOrNull<T>(
  path: string,
  init?: RequestInit,
): Promise<T | null> {
  return requestJsonOrNull<T>(gatewayBaseUrl, path, init);
}
