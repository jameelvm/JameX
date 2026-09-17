import { publicGatewayBaseUrl } from "@/lib/config";
import { requestJson, requestJsonOrNull, requestVoid } from "@/lib/api/errors";

export { ApiError } from "@/lib/api/errors";

/**
 * Gateway calls made from the browser — Client Components with no server
 * render to pass data down from (the viewer-identity stub, later click
 * handlers for reactions and comments). Reads the `NEXT_PUBLIC_*` base URL so
 * it actually resolves in the bundle shipped to the browser.
 */
export function browserApiFetch<T>(
  path: string,
  init?: RequestInit,
): Promise<T> {
  return requestJson<T>(publicGatewayBaseUrl, path, init);
}

/** Same as {@link browserApiFetch}, but a 404 resolves to `null` instead of throwing. */
export function browserApiFetchOrNull<T>(
  path: string,
  init?: RequestInit,
): Promise<T | null> {
  return requestJsonOrNull<T>(publicGatewayBaseUrl, path, init);
}

/** For mutations with no response body — see {@link requestVoid}. */
export function browserApiMutate(
  path: string,
  init?: RequestInit,
): Promise<void> {
  return requestVoid(publicGatewayBaseUrl, path, init);
}
