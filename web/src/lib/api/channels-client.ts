import { browserApiFetch } from "@/lib/api/browser-client";
import type { Channel } from "@/types/channel";

/** Every channel a user owns — Identity has no notion of "the" channel, only "a user's channels". */
export function getMyChannels(userId: string): Promise<Channel[]> {
  return browserApiFetch<Channel[]>(`/users/${userId}/channels`);
}

/**
 * The owner is never in the request body — Identity's `POST /channels`
 * reads it from the caller's own validated identity (see
 * `ChannelsController.Create` on the backend), attached automatically as a
 * bearer token by `lib/api/browser-client.ts`. Passing an owner id here
 * would do nothing but invite the question of what happens if it disagreed
 * with the token.
 */
export function createChannel(name: string, handle: string): Promise<Channel> {
  return browserApiFetch<Channel>("/channels", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name, handle, avatarUrl: null }),
  });
}
