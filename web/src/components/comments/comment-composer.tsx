"use client";

import { useState, type FormEvent } from "react";

interface CommentComposerProps {
  onSubmit: (text: string) => Promise<void>;
  placeholder: string;
  submitLabel: string;
  disabled: boolean;
  disabledReason?: string;
  autoFocus?: boolean;
  onCancel?: () => void;
  /** Pre-fills the field — the same form doubles as the edit-comment UI, not just new comments/replies. */
  initialText?: string;
}

/**
 * The one text-entry form for a new top-level comment, a reply, and editing
 * an existing comment — all three only differ in what `onSubmit` does with
 * the text and whether a value is already in the field, not in how the text
 * gets typed and submitted.
 */
export function CommentComposer({
  onSubmit,
  placeholder,
  submitLabel,
  disabled,
  disabledReason,
  autoFocus,
  onCancel,
  initialText = "",
}: CommentComposerProps) {
  const [text, setText] = useState(initialText);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const trimmed = text.trim();
  const canSubmit = !disabled && !isSubmitting && trimmed.length > 0;

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (!canSubmit) return;

    setIsSubmitting(true);
    try {
      await onSubmit(trimmed);
      setText("");
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-2">
      <textarea
        value={text}
        onChange={(event) => setText(event.target.value)}
        placeholder={disabled ? (disabledReason ?? placeholder) : placeholder}
        disabled={disabled || isSubmitting}
        autoFocus={autoFocus}
        rows={2}
        maxLength={10_000}
        className="w-full resize-none border-b border-neutral-300 bg-transparent px-1 py-2 text-sm text-neutral-900 placeholder:text-neutral-500 focus:border-neutral-900 focus:outline-none disabled:opacity-60"
      />
      <div className="flex justify-end gap-2">
        {onCancel && (
          <button
            type="button"
            onClick={onCancel}
            className="rounded-full px-3 py-1.5 text-sm font-medium text-neutral-700 hover:bg-neutral-100"
          >
            Cancel
          </button>
        )}
        <button
          type="submit"
          disabled={!canSubmit}
          className="rounded-full bg-blue-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-neutral-200 disabled:text-neutral-400"
        >
          {submitLabel}
        </button>
      </div>
    </form>
  );
}
