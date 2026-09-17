import { browserApiMutate } from "@/lib/api/browser-client";
import { VIEWER_HEADER_NAME } from "@/lib/api/headers";
import { ReactionKind } from "@/types/video";

/**
 * Sets or switches the viewer's reaction. PUT, not POST — reacting is
 * idempotent (see `ReactionService.SetReactionAsync` on the backend): calling
 * this twice with the same `kind` leaves the video in the state it was
 * already in.
 */
export function setMyReaction(
  videoId: string,
  viewerId: string,
  kind: ReactionKind,
): Promise<void> {
  return browserApiMutate(`/videos/${videoId}/reactions/me`, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      [VIEWER_HEADER_NAME]: viewerId,
    },
    body: JSON.stringify({ kind }),
  });
}

/** Withdraws the viewer's reaction entirely — the "click Like again to un-like" case. */
export function removeMyReaction(
  videoId: string,
  viewerId: string,
): Promise<void> {
  return browserApiMutate(`/videos/${videoId}/reactions/me`, {
    method: "DELETE",
    headers: { [VIEWER_HEADER_NAME]: viewerId },
  });
}
