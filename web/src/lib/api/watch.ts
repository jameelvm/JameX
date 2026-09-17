import { cache } from "react";

import { apiFetchOrNull } from "@/lib/api/client";
import { VIEWER_HEADER_NAME } from "@/lib/api/headers";
import type { VideoDetail } from "@/types/video";

/**
 * The watch page in one call — see `WatchController`/`WatchAggregationService`
 * on the Gateway. `null` means the video does not exist (or was deleted);
 * that is a real, expected outcome here, not a fault.
 *
 * `viewerId` — from `getServerViewerId()`, the id-cookie mirror of the
 * client-side viewer — is forwarded as `X-JameX-User` so the Gateway can
 * resolve `viewerReaction` server-side, the same way it would for a real
 * authenticated session. Without it, every server-rendered load looks
 * anonymous and the caller's own reaction never shows as active until some
 * client-side re-fetch fixes it up after the fact — found by actually
 * clicking a reaction and reloading, not assumed.
 *
 * Wrapped in React's `cache()` so `generateMetadata` and the page component
 * — which both need this video, for the same viewer — share one fetch per
 * request instead of two. This dedupes within a single render only; it is
 * not a data cache, and `cache: "no-store"` below still holds across
 * requests.
 */
export const getWatchPage = cache(
  (videoId: string, viewerId?: string): Promise<VideoDetail | null> =>
    apiFetchOrNull<VideoDetail>(`/watch/${videoId}`, {
      // The watch page is never static — views and reactions change on
      // every request, and this is a metadata service, not a CDN-cached asset.
      cache: "no-store",
      headers: viewerId ? { [VIEWER_HEADER_NAME]: viewerId } : undefined,
    }),
);
