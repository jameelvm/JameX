import { cookies } from "next/headers";

import { VIEWER_TOKEN_COOKIE_NAME } from "@/lib/viewer/cookie";

/**
 * The one way a Server Component learns who is viewing. `localStorage`
 * — where `ViewerProvider` keeps the full session — does not exist on the
 * server; a cookie does, because it rides along with every request
 * automatically. The token itself is mirrored here, not just a user id: the
 * Gateway now derives identity purely from a validated JWT (see
 * `GatewayRegistrationExtensions.AddJameXJwtBearer` on the backend) and
 * strips any client-supplied identity header outright, so a server-rendered
 * request has to present the real credential, not just claim a user id.
 *
 * `undefined` for a viewer with no cookie — either never signed in, or
 * signed out. Every caller must treat that the same as "anonymous", not as
 * an error: browsing stays open with no viewer at all.
 */
export async function getServerAuthToken(): Promise<string | undefined> {
  const cookieStore = await cookies();
  return cookieStore.get(VIEWER_TOKEN_COOKIE_NAME)?.value;
}
