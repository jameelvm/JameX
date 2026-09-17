import { apiFetch } from "@/lib/api/client";
import { hydrateChannelNames } from "@/lib/api/channel-hydration";
import type { PagedResult } from "@/types/api";
import type { VideoSummary, VideoSummaryWithChannel } from "@/types/video";

/**
 * The home feed — see Catalog's `GET /videos`, restricted to public/Ready
 * videos server-side. Never cached: a freshly published or newly-liked
 * video should show up without a stale intermediate copy in the way.
 */
export async function getVideoFeed(
  page: number,
  pageSize: number,
): Promise<PagedResult<VideoSummaryWithChannel>> {
  const result = await apiFetch<PagedResult<VideoSummary>>(
    `/videos?page=${page}&pageSize=${pageSize}`,
    { cache: "no-store" },
  );

  return { ...result, items: await hydrateChannelNames(result.items) };
}
