import { cookies } from "next/headers";

import { VIEWER_ID_COOKIE_NAME } from "@/lib/viewer/cookie";

/**
 * The one way a Server Component learns who is viewing. `localStorage`
 * — where `ViewerProvider` keeps the full identity — does not exist on the
 * server; a cookie does, because it rides along with every request
 * automatically. Only the id is ever mirrored here, never the display name:
 * the server only ever needs enough to forward `X-JameX-User`.
 *
 * `undefined` for a viewer with no cookie yet (their very first request,
 * before `ViewerProvider` has run client-side) — every caller must treat
 * that the same as "anonymous", not as an error.
 */
export async function getServerViewerId(): Promise<string | undefined> {
  const cookieStore = await cookies();
  return cookieStore.get(VIEWER_ID_COOKIE_NAME)?.value;
}
