import { cache } from "react";

import { apiFetchOrNull } from "@/lib/api/client";
import type { VideoDetail } from "@/types/video";

/**
 * The watch page in one call — see `WatchController`/`WatchAggregationService`
 * on the Gateway. `null` means the video does not exist (or was deleted);
 * that is a real, expected outcome here, not a fault.
 *
 * `authToken` — from `getServerAuthToken()`, the cookie mirror of the
 * client-side session — is forwarded as a real `Authorization: Bearer`
 * header so the Gateway can validate it and resolve `viewerReaction`
 * server-side. This used to forward a bare, client-claimed user id as
 * `X-JameX-User` directly; the Gateway now strips that header outright and
 * only ever sets it from a validated token's own subject claim (see
 * `GatewayRegistrationExtensions.AddJameXJwtBearer`), so a server-rendered
 * request has to present the real credential to be recognised at all.
 * Without it, every server-rendered load looks anonymous and the caller's
 * own reaction never shows as active until some client-side re-fetch fixes
 * it up after the fact — found by actually clicking a reaction and
 * reloading, not assumed.
 *
 * Wrapped in React's `cache()` so `generateMetadata` and the page component
 * — which both need this video, for the same viewer — share one fetch per
 * request instead of two. This dedupes within a single render only; it is
 * not a data cache, and `cache: "no-store"` below still holds across
 * requests.
 */
export const getWatchPage = cache(
  (videoId: string, authToken?: string): Promise<VideoDetail | null> =>
    apiFetchOrNull<VideoDetail>(`/watch/${videoId}`, {
      // The watch page is never static — views and reactions change on
      // every request, and this is a metadata service, not a CDN-cached asset.
      cache: "no-store",
      headers: authToken ? { Authorization: `Bearer ${authToken}` } : undefined,
    }),
);
