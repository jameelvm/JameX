/**
 * The viewer id, mirrored into a cookie alongside `localStorage` — see
 * `lib/viewer/server-viewer.ts` for why. Client-only (`document.cookie`);
 * the server-side read lives in that separate file so this one never pulls
 * in `next/headers`, which cannot be bundled for the browser.
 */
export const VIEWER_ID_COOKIE_NAME = "jamex_viewer_id";

const ONE_YEAR_IN_SECONDS = 60 * 60 * 24 * 365;

export function writeViewerIdCookie(userId: string): void {
  document.cookie = `${VIEWER_ID_COOKIE_NAME}=${userId}; path=/; max-age=${ONE_YEAR_IN_SECONDS}; samesite=lax`;
}

export function clearViewerIdCookie(): void {
  document.cookie = `${VIEWER_ID_COOKIE_NAME}=; path=/; max-age=0`;
}
