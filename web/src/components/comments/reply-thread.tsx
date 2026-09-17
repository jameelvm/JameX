"use client";

import { useState } from "react";

import { CommentComposer } from "@/components/comments/comment-composer";
import { CommentItem } from "@/components/comments/comment-item";
import { useViewer } from "@/components/viewer/viewer-provider";
import {
  deleteComment,
  getReplies,
  postComment,
  updateComment,
} from "@/lib/api/comments-client";
import { isCommentDeleted } from "@/lib/comments";
import type { Comment } from "@/types/comment";

/** A reply, plus a local override for one this session just deleted — see the identical remark on `CommentState` in `CommentsSection`. */
type ReplyState = Comment & { isDeleted?: boolean };

interface ReplyThreadProps {
  videoId: string;
  parentCommentId: string;
}

/**
 * Everything about a top-level comment's replies — viewing them, adding one
 * — lives here, self-contained. A reply is never rendered anywhere else, so
 * there is no shared state to keep in sync with `CommentsSection`; the
 * fetched replies and the reply composer both belong to this component
 * alone.
 *
 * There is deliberately no reply count shown before expanding: `CommentDto`
 * carries none, so "View replies" is a plain toggle, not "View 3 replies".
 * Naming that gap is better than a fake or stale-prone count.
 */
export function ReplyThread({ videoId, parentCommentId }: ReplyThreadProps) {
  const { viewer } = useViewer();
  const [isExpanded, setIsExpanded] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [replies, setReplies] = useState<ReplyState[] | null>(null);
  const [isReplying, setIsReplying] = useState(false);

  async function loadReplies() {
    setIsLoading(true);
    try {
      const page = await getReplies(videoId, parentCommentId);
      setReplies(page.items);
    } finally {
      setIsLoading(false);
    }
  }

  function handleExpand() {
    setIsExpanded(true);
    if (replies === null) void loadReplies();
  }

  async function handlePostReply(text: string) {
    if (!viewer) return;

    const created = await postComment(videoId, viewer.userId, text, parentCommentId);
    setIsExpanded(true);
    setReplies((current) => [...(current ?? []), created]);
    setIsReplying(false);
  }

  async function handleEdit(commentId: string, text: string) {
    if (!viewer) return;

    const updated = await updateComment(videoId, commentId, viewer.userId, text);
    setReplies((current) =>
      current?.map((reply) => (reply.commentId === commentId ? updated : reply)) ?? null,
    );
  }

  async function handleDelete(commentId: string) {
    if (!viewer) return;

    await deleteComment(videoId, commentId, viewer.userId);
    setReplies((current) =>
      current?.map((reply) =>
        reply.commentId === commentId ? { ...reply, isDeleted: true } : reply,
      ) ?? null,
    );
  }

  return (
    <div className="ml-11 flex flex-col gap-2">
      <div className="flex gap-4 text-xs font-medium text-blue-700">
        {!isExpanded && (
          <button
            type="button"
            onClick={handleExpand}
            className="hover:text-blue-800"
          >
            View replies
          </button>
        )}
        <button
          type="button"
          onClick={() => setIsReplying((current) => !current)}
          className="hover:text-blue-800"
        >
          Reply
        </button>
      </div>

      {isReplying && (
        <CommentComposer
          placeholder="Add a reply…"
          submitLabel="Reply"
          disabled={viewer === null}
          disabledReason="Signing you in…"
          autoFocus
          onCancel={() => setIsReplying(false)}
          onSubmit={handlePostReply}
        />
      )}

      {isExpanded && (
        <div className="flex flex-col gap-3">
          {isLoading && (
            <span className="text-xs text-neutral-500">Loading replies…</span>
          )}
          {!isLoading && replies?.length === 0 && (
            <span className="text-xs text-neutral-500">No replies yet.</span>
          )}
          {replies?.map((reply) => (
            <CommentItem
              key={reply.commentId}
              comment={reply}
              isDeleted={reply.isDeleted ?? isCommentDeleted(reply)}
              isOwn={viewer?.userId === reply.userId}
              onEdit={(text) => handleEdit(reply.commentId, text)}
              onDelete={() => handleDelete(reply.commentId)}
            />
          ))}
        </div>
      )}
    </div>
  );
}
