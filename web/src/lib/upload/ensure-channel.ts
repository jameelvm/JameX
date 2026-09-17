import { createChannel, getMyChannels } from "@/lib/api/channels-client";
import type { Viewer } from "@/types/viewer";

/**
 * Uploading needs a channel to publish to, and this app has no "create a
 * channel" flow of its own — there is exactly one implicit channel per
 * guest viewer, provisioned the first time it's actually needed (uploading),
 * not eagerly alongside the guest user itself. Mirrors the same
 * get-or-create shape `ViewerProvider` uses for the user underneath it.
 */
export async function ensureMyChannel(viewer: Viewer): Promise<string> {
  const existing = await getMyChannels(viewer.userId);
  if (existing.length > 0) return existing[0].channelId;

  const created = await createChannel(
    viewer.userId,
    `${viewer.displayName}'s channel`,
    `${viewer.displayName.toLowerCase()}-${viewer.userId.slice(0, 8)}`,
  );
  return created.channelId;
}
