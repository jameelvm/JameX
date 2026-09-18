/**
 * The auth token, mirrored into a cookie alongside `localStorage` — see
 * `lib/viewer/server-viewer.ts` for why. Client-only (`document.cookie`);
 * the server-side read lives in that separate file so this one never pulls
 * in `next/headers`, which cannot be bundled for the browser.
 *
 * Plain, not `httpOnly` — it has to be, since only server-issued responses
 * can set an `httpOnly` cookie, and this is written from the browser right
 * after a client-side login/signup call resolves. That is a real trade-off
 * (an XSS vulnerability could read this token) accepted deliberately for a
 * project outside auth's documented scope, not overlooked; a production
 * deployment would issue this cookie from a server-side login route instead.
 */
export const VIEWER_TOKEN_COOKIE_NAME = "jamex_token";

const ONE_YEAR_IN_SECONDS = 60 * 60 * 24 * 365;

export function writeViewerTokenCookie(token: string): void {
  document.cookie = `${VIEWER_TOKEN_COOKIE_NAME}=${token}; path=/; max-age=${ONE_YEAR_IN_SECONDS}; samesite=lax`;
}

export function clearViewerTokenCookie(): void {
  document.cookie = `${VIEWER_TOKEN_COOKIE_NAME}=; path=/; max-age=0`;
}
