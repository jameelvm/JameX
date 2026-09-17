import { browserApiFetch, browserApiMutate } from "@/lib/api/browser-client";
import { VIEWER_HEADER_NAME } from "@/lib/api/headers";
import type { Comment } from "@/types/comment";
import type { PagedResult } from "@/types/api";

/** The browser-side counterpart to `lib/api/comments.ts#getComments` — used only for "Load more", after the server-rendered first page. */
export function getCommentsPage(
  videoId: string,
  page: number,
  pageSize = 20,
): Promise<PagedResult<Comment>> {
  return browserApiFetch<PagedResult<Comment>>(
    `/videos/${videoId}/comments?page=${page}&pageSize=${pageSize}`,
  );
}

/**
 * Loaded on demand when a viewer expands a comment's replies — never
 * preloaded with the top-level page. `videoId` is required by the route
 * (`videos/{videoId}/comments/{commentId}/replies`) even though replies are
 * really looked up by `commentId` alone — the controller is scoped under a
 * video either way.
 */
export function getReplies(
  videoId: string,
  commentId: string,
  page = 1,
  pageSize = 20,
): Promise<PagedResult<Comment>> {
  return browserApiFetch<PagedResult<Comment>>(
    `/videos/${videoId}/comments/${commentId}/replies?page=${page}&pageSize=${pageSize}`,
  );
}

/**
 * Posts a comment, or a reply when `parentCommentId` is set — same endpoint,
 * same as the backend's own `CreateCommentRequest` shape.
 */
export function postComment(
  videoId: string,
  viewerId: string,
  text: string,
  parentCommentId?: string,
): Promise<Comment> {
  return browserApiFetch<Comment>(`/videos/${videoId}/comments`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      [VIEWER_HEADER_NAME]: viewerId,
    },
    body: JSON.stringify({ text, parentCommentId: parentCommentId ?? null }),
  });
}

/** `videoId` is only needed to build the URL — the backend authorises by comment ownership, not by video. */
export function updateComment(
  videoId: string,
  commentId: string,
  viewerId: string,
  text: string,
): Promise<Comment> {
  return browserApiFetch<Comment>(
    `/videos/${videoId}/comments/${commentId}`,
    {
      method: "PATCH",
      headers: {
        "Content-Type": "application/json",
        [VIEWER_HEADER_NAME]: viewerId,
      },
      body: JSON.stringify({ text }),
    },
  );
}

/**
 * Deletes a comment — a tombstone if it has replies, a physical removal
 * otherwise (see `CommentService.DeleteAsync` on the backend). This call
 * cannot tell which happened from its response alone (204 either way), which
 * is why the UI treats every successful delete as "show '[deleted]'" rather
 * than "remove from the list" — the one behaviour that is correct for both
 * outcomes without a follow-up fetch.
 */
export function deleteComment(
  videoId: string,
  commentId: string,
  viewerId: string,
): Promise<void> {
  return browserApiMutate(`/videos/${videoId}/comments/${commentId}`, {
    method: "DELETE",
    headers: { [VIEWER_HEADER_NAME]: viewerId },
  });
}
