import { apiFetch } from "@/lib/api/client";
import type { PagedResult } from "@/types/api";
import type { VideoSummary } from "@/types/video";

/**
 * "Your videos" — see `VideosController.GetMine` on the backend. Every
 * status and privacy level, unlike the home feed and search, because the
 * caller is always looking at their own uploads.
 * <para>
 * Server-rendered, the same as the watch page, forwarding the token cookie
 * mirror as a real `Authorization: Bearer` header rather than fetching this
 * client-side — there is no anonymous version of this page to render first,
 * so there is nothing to gain by waiting for a browser round trip once the
 * page itself has already loaded.
 * </para>
 */
export function getMyVideos(
  authToken: string,
  page = 1,
  pageSize = 24,
): Promise<PagedResult<VideoSummary>> {
  return apiFetch<PagedResult<VideoSummary>>(`/videos/mine?page=${page}&pageSize=${pageSize}`, {
    cache: "no-store",
    headers: { Authorization: `Bearer ${authToken}` },
  });
}
