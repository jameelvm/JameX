import { apiFetchOrNull } from "@/lib/api/client";
import type { Channel } from "@/types/channel";

/**
 * Resolves a batch of channel ids to display names, one parallel request per
 * *distinct* channel — not one per video, since a feed page routinely
 * repeats the same channel across several videos. The same reasoning the
 * Gateway's own watch aggregation already uses for a single video: the
 * caller here is a Server Component already blocked on this response, so
 * the extra hop costs nothing structurally new. A channel Identity can't
 * resolve (deleted, or a transient fault) degrades that one video's
 * `channelName` to `null` rather than failing the whole feed — matching how
 * `ChannelByline` already renders a null name as "Unknown channel".
 */
export async function hydrateChannelNames<T extends { channelId: string }>(
  items: T[],
): Promise<(T & { channelName: string | null })[]> {
  const uniqueChannelIds = [...new Set(items.map((item) => item.channelId))];

  const entries = await Promise.all(
    uniqueChannelIds.map(async (channelId) => {
      const channel = await apiFetchOrNull<Channel>(`/channels/${channelId}`).catch(
        () => null,
      );
      return [channelId, channel?.name ?? null] as const;
    }),
  );
  const namesByChannelId = new Map(entries);

  return items.map((item) => ({
    ...item,
    channelName: namesByChannelId.get(item.channelId) ?? null,
  }));
}
