"use client";

import { useState } from "react";

import { CommentComposer } from "@/components/comments/comment-composer";
import { CommentItem } from "@/components/comments/comment-item";
import { ReplyThread } from "@/components/comments/reply-thread";
import { useViewer } from "@/components/viewer/viewer-provider";
import {
  deleteComment,
  getCommentsPage,
  postComment,
  updateComment,
} from "@/lib/api/comments-client";
import { isCommentDeleted } from "@/lib/comments";
import type { Comment } from "@/types/comment";
import type { PagedResult } from "@/types/api";

/**
 * A top-level comment, plus a local override for one this session just
 * deleted. Rendering falls back to `isCommentDeleted(comment)` when this is
 * unset — a comment that was already `"[deleted]"` when it arrived from the
 * server (deleted in an earlier session, or by someone else) must render
 * the same way as one deleted just now, not show live Edit/Delete controls
 * because this particular tab never personally deleted it.
 */
type CommentState = Comment & { isDeleted?: boolean };

interface CommentsSectionProps {
  videoId: string;
  initialComments: PagedResult<Comment>;
}

/**
 * The top-level comment list, seeded from the server-rendered first page
 * (see `getComments`) and managed client-side from there — posting,
 * editing, and deleting a top-level comment all update this component's own
 * state rather than triggering a full page refetch. Each comment's replies
 * are `ReplyThread`'s concern entirely; this component never looks inside
 * them.
 */
export function CommentsSection({
  videoId,
  initialComments,
}: CommentsSectionProps) {
  const { viewer } = useViewer();
  const [comments, setComments] = useState<CommentState[]>(
    initialComments.items,
  );
  const [page, setPage] = useState(initialComments.page);
  const [hasMore, setHasMore] = useState(initialComments.hasMore);
  const [isLoadingMore, setIsLoadingMore] = useState(false);

  async function handlePostComment(text: string) {
    if (!viewer) return;

    const created = await postComment(videoId, text);
    setComments((current) => [created, ...current]);
  }

  async function handleEdit(commentId: string, text: string) {
    if (!viewer) return;

    const updated = await updateComment(videoId, commentId, text);
    setComments((current) =>
      current.map((comment) =>
        comment.commentId === commentId ? updated : comment,
      ),
    );
  }

  async function handleDelete(commentId: string) {
    if (!viewer) return;

    await deleteComment(videoId, commentId);
    setComments((current) =>
      current.map((comment) =>
        comment.commentId === commentId
          ? { ...comment, isDeleted: true }
          : comment,
      ),
    );
  }

  async function handleLoadMore() {
    setIsLoadingMore(true);
    try {
      const nextPage = await getCommentsPage(videoId, page + 1);
      setComments((current) => [...current, ...nextPage.items]);
      setPage(nextPage.page);
      setHasMore(nextPage.hasMore);
    } finally {
      setIsLoadingMore(false);
    }
  }

  return (
    <section className="flex flex-col gap-6">
      <h2 className="text-sm font-semibold text-neutral-900">Comments</h2>

      <CommentComposer
        placeholder="Add a comment…"
        submitLabel="Comment"
        disabled={viewer === null}
        disabledReason="Sign in to comment"
        onSubmit={handlePostComment}
      />

      <div className="flex flex-col gap-6">
        {comments.map((comment) => (
          <CommentItem
            key={comment.commentId}
            comment={comment}
            isDeleted={comment.isDeleted ?? isCommentDeleted(comment)}
            isOwn={viewer?.userId === comment.userId}
            onEdit={(text) => handleEdit(comment.commentId, text)}
            onDelete={() => handleDelete(comment.commentId)}
          >
            <ReplyThread videoId={videoId} parentCommentId={comment.commentId} />
          </CommentItem>
        ))}
      </div>

      {hasMore && (
        <button
          type="button"
          onClick={handleLoadMore}
          disabled={isLoadingMore}
          className="self-start text-sm font-medium text-blue-700 hover:text-blue-800 disabled:opacity-50"
        >
          {isLoadingMore ? "Loading…" : "Load more comments"}
        </button>
      )}
    </section>
  );
}
