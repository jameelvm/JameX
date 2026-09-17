import type { Comment } from "@/types/comment";

/**
 * The backend's literal tombstone text — see `CommentMapping.ToDto` and
 * `Comment.IsDeleted` on the Engagement service. There is no separate
 * `isDeleted` field on the wire; this string *is* the signal, so every place
 * that turns a fetched `Comment` into local UI state must check for it, not
 * just the comments a session personally deleted in this browser tab.
 */
export const DELETED_COMMENT_TEXT = "[deleted]";

export function isCommentDeleted(comment: Pick<Comment, "text">): boolean {
  return comment.text === DELETED_COMMENT_TEXT;
}
