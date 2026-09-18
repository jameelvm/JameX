import { publicGatewayBaseUrl } from "@/lib/config";
import { requestJson, requestJsonOrNull, requestVoid } from "@/lib/api/errors";
import { readStoredViewer } from "@/lib/viewer/storage";

export { ApiError } from "@/lib/api/errors";

/**
 * Every authenticated browser call goes through here, so the token is
 * attached in exactly one place rather than each caller (reactions,
 * comments, uploads, channels) building its own `Authorization` header from
 * a `viewerId` it had to be handed. Reading `localStorage` directly rather
 * than going through React context is deliberate: these functions are
 * plain, callable outside any component, and the alternative — threading a
 * token through every function signature in `lib/api/*-client.ts` — is
 * exactly the ceremony this centralisation removes.
 * <para>
 * A request with no signed-in viewer simply carries no `Authorization`
 * header at all — the Gateway then has nothing to validate, `X-JameX-User`
 * never gets set, and any endpoint requiring a caller correctly 401s. That
 * is the intended anonymous-browsing path, not an error case to special-case
 * here.
 * </para>
 */
function authHeaders(): HeadersInit {
  const viewer = readStoredViewer();
  return viewer ? { Authorization: `Bearer ${viewer.token}` } : {};
}

/**
 * Gateway calls made from the browser — Client Components with no server
 * render to pass data down from (reactions, comments, uploads, and the auth
 * forms themselves). Reads the `NEXT_PUBLIC_*` base URL so it actually
 * resolves in the bundle shipped to the browser.
 */
export function browserApiFetch<T>(
  path: string,
  init?: RequestInit,
): Promise<T> {
  return requestJson<T>(publicGatewayBaseUrl, path, {
    ...init,
    headers: { ...authHeaders(), ...init?.headers },
  });
}

/** Same as {@link browserApiFetch}, but a 404 resolves to `null` instead of throwing. */
export function browserApiFetchOrNull<T>(
  path: string,
  init?: RequestInit,
): Promise<T | null> {
  return requestJsonOrNull<T>(publicGatewayBaseUrl, path, {
    ...init,
    headers: { ...authHeaders(), ...init?.headers },
  });
}

/** For mutations with no response body — see {@link requestVoid}. */
export function browserApiMutate(
  path: string,
  init?: RequestInit,
): Promise<void> {
  return requestVoid(publicGatewayBaseUrl, path, {
    ...init,
    headers: { ...authHeaders(), ...init?.headers },
  });
}
