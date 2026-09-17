import { browserApiFetch } from "@/lib/api/browser-client";
import { VIEWER_HEADER_NAME } from "@/lib/api/headers";
import type { Channel } from "@/types/channel";

/** Every channel a user owns — Identity has no notion of "the" channel, only "a user's channels". */
export function getMyChannels(userId: string): Promise<Channel[]> {
  return browserApiFetch<Channel[]>(`/users/${userId}/channels`);
}

export function createChannel(
  ownerUserId: string,
  name: string,
  handle: string,
): Promise<Channel> {
  return browserApiFetch<Channel>("/channels", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      [VIEWER_HEADER_NAME]: ownerUserId,
    },
    body: JSON.stringify({ name, handle, avatarUrl: null }),
  });
}
