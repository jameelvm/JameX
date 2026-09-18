import { browserApiMutate } from "@/lib/api/browser-client";
import { ReactionKind } from "@/types/video";

/**
 * Sets or switches the viewer's reaction. PUT, not POST — reacting is
 * idempotent (see `ReactionService.SetReactionAsync` on the backend): calling
 * this twice with the same `kind` leaves the video in the state it was
 * already in. The caller is identified by the signed-in viewer's token,
 * attached automatically — see `lib/api/browser-client.ts`.
 */
export function setMyReaction(
  videoId: string,
  kind: ReactionKind,
): Promise<void> {
  return browserApiMutate(`/videos/${videoId}/reactions/me`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ kind }),
  });
}

/** Withdraws the viewer's reaction entirely — the "click Like again to un-like" case. */
export function removeMyReaction(videoId: string): Promise<void> {
  return browserApiMutate(`/videos/${videoId}/reactions/me`, {
    method: "DELETE",
  });
}
