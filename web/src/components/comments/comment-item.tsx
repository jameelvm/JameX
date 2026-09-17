"use client";

import { useState, type ReactNode } from "react";

import { CommentComposer } from "@/components/comments/comment-composer";
import { Avatar } from "@/components/common/avatar";
import { DELETED_COMMENT_TEXT } from "@/lib/comments";
import { formatPublishedDate } from "@/lib/format";
import type { Comment } from "@/types/comment";

interface CommentItemProps {
  comment: Comment;
  /**
   * Set locally once a delete succeeds — see `deleteComment`'s remarks for
   * why the UI cannot tell a tombstone from a physical removal, and treats
   * both the same way.
   */
  isDeleted: boolean;
  isOwn: boolean;
  onEdit: (text: string) => Promise<void>;
  onDelete: () => Promise<void>;
  /** Where a top-level comment renders its `ReplyThread` — left out of a reply, which cannot itself have replies. */
  children?: ReactNode;
}

/** Renders exactly one comment's own text and actions — reply-thread concerns belong to `ReplyThread`, not here. */
export function CommentItem({
  comment,
  isDeleted,
  isOwn,
  onEdit,
  onDelete,
  children,
}: CommentItemProps) {
  const [isEditing, setIsEditing] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);

  async function handleDelete() {
    setIsDeleting(true);
    try {
      await onDelete();
    } finally {
      setIsDeleting(false);
    }
  }

  return (
    <div className="flex gap-3">
      <Avatar name={comment.userDisplayName} />
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <div className="flex items-baseline gap-2 text-sm">
          <span className="font-medium text-neutral-900">
            {comment.userDisplayName ?? "Guest"}
          </span>
          <span className="text-xs text-neutral-500">
            {formatPublishedDate(comment.createdAt)}
            {comment.isEdited && !isDeleted && " (edited)"}
          </span>
        </div>

        {isEditing ? (
          <CommentComposer
            initialText={comment.text}
            placeholder="Edit your comment"
            submitLabel="Save"
            disabled={false}
            autoFocus
            onCancel={() => setIsEditing(false)}
            onSubmit={async (text) => {
              await onEdit(text);
              setIsEditing(false);
            }}
          />
        ) : (
          <p
            className={
              isDeleted
                ? "text-sm italic text-neutral-500"
                : "whitespace-pre-line text-sm text-neutral-800"
            }
          >
            {isDeleted ? DELETED_COMMENT_TEXT : comment.text}
          </p>
        )}

        {!isEditing && isOwn && !isDeleted && (
          <div className="flex gap-3 text-xs font-medium text-neutral-500">
            <button
              type="button"
              onClick={() => setIsEditing(true)}
              className="hover:text-neutral-800"
            >
              Edit
            </button>
            <button
              type="button"
              onClick={handleDelete}
              disabled={isDeleting}
              className="hover:text-neutral-800 disabled:opacity-50"
            >
              Delete
            </button>
          </div>
        )}

        {children}
      </div>
    </div>
  );
}
